using CodeSwitch.Utils.EmailQueuer.Delivery;
using FluentEmail.Core;
using FluentEmail.Core.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CodeSwitch.Utils.EmailQueuer.Tests
{
    public class SmtpDeliveryTests
    {
        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

        private static SmtpClient Template(int port) => new SmtpClient
        {
            Host = "127.0.0.1",
            Port = port,
            EnableSsl = false,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential("sender@test.local", "x")
        };

        private static EmailData Data(string to = "someone@test.local") => new Email("sender@test.local")
            .To(to)
            .Subject("Hello")
            .Body("<p>Hi</p>", true)
            .Data;

        private static async Task<(TimeoutException Error, TimeSpan Elapsed)> TimedFailure(SmtpDelivery delivery)
        {
            var sw = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<TimeoutException>(() => delivery.SendAsync(Data()));
            return (error, sw.Elapsed);
        }

        [Fact]
        public async Task Relay_that_accepts_the_connection_and_never_answers_fails_within_the_timeout()
        {
            using var relay = new FakeSmtpServer(n => n == 1 ? RelayBehavior.SilentFromStart : RelayBehavior.Normal);
            using var delivery = new SmtpDelivery(Template(relay.Port), SendTimeout, TimeSpan.FromSeconds(30), 50);

            var (error, elapsed) = await TimedFailure(delivery);

            Assert.InRange(elapsed, SendTimeout - TimeSpan.FromMilliseconds(200), SendTimeout + TimeSpan.FromSeconds(3));
            Assert.Contains("did not complete within 2 seconds", error.Message);

            // The next send does not inherit the dead connection.
            await delivery.SendAsync(Data());
            Assert.Equal(2, relay.Connections);
            Assert.Equal(1, relay.Messages);
        }

        [Fact]
        public async Task Reused_connection_that_goes_silent_after_MAIL_FROM_fails_within_the_timeout_and_the_next_send_reconnects()
        {
            // The dev failure: a first send succeeds, the connection is kept, and the next MAIL FROM
            // on it never gets an answer.
            using var relay = new FakeSmtpServer(n => n == 1 ? RelayBehavior.SilentAfterFirstMessage : RelayBehavior.Normal);
            using var delivery = new SmtpDelivery(Template(relay.Port), SendTimeout, TimeSpan.FromSeconds(30), 50);

            await delivery.SendAsync(Data());
            Assert.Equal(1, relay.Connections);

            var (_, elapsed) = await TimedFailure(delivery);
            Assert.InRange(elapsed, SendTimeout - TimeSpan.FromMilliseconds(200), SendTimeout + TimeSpan.FromSeconds(3));

            await delivery.SendAsync(Data());
            Assert.Equal(2, relay.Connections);
            Assert.Equal(2, relay.Messages);
            Assert.Equal(2, delivery.ClientsCreated);
        }

        [Fact]
        public async Task Sends_close_together_share_one_connection_closed_with_QUIT()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            using (var delivery = new SmtpDelivery(Template(relay.Port), SendTimeout, TimeSpan.FromSeconds(30), 50))
            {
                for (var i = 0; i < 3; i++)
                    await delivery.SendAsync(Data());
                Assert.Equal(1, delivery.ClientsCreated);
            }
            Assert.Equal(1, relay.Connections);
            Assert.Equal(3, relay.Messages);
            await WaitFor(() => relay.Quits == 1);
        }

        [Fact]
        public async Task An_idle_connection_is_closed_with_QUIT_and_replaced()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            using var delivery = new SmtpDelivery(Template(relay.Port), SendTimeout, TimeSpan.FromMilliseconds(200), 50);

            await delivery.SendAsync(Data());
            await Task.Delay(500);
            await delivery.SendAsync(Data());

            Assert.Equal(2, relay.Connections);
            Assert.Equal(2, relay.Messages);
            await WaitFor(() => relay.Quits >= 1);
        }

        [Fact]
        public async Task A_connection_is_replaced_after_the_message_cap()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            using var delivery = new SmtpDelivery(Template(relay.Port), SendTimeout, TimeSpan.FromSeconds(30), 2);

            for (var i = 0; i < 5; i++)
                await delivery.SendAsync(Data());

            Assert.Equal(3, relay.Connections);
            Assert.Equal(5, relay.Messages);
        }

        [Fact]
        public void Template_settings_are_copied_to_the_client_that_sends()
        {
            var template = Template(2525);
            template.EnableSsl = true;
            template.Timeout = 12345;
            using var copy = SmtpDelivery.CreateClient(template);

            Assert.Equal("127.0.0.1", copy.Host);
            Assert.Equal(2525, copy.Port);
            Assert.True(copy.EnableSsl);
            Assert.Equal(12345, copy.Timeout);
            Assert.Same(template.Credentials, copy.Credentials);
            Assert.False(copy.UseDefaultCredentials);
            Assert.NotSame(template, copy);
        }

        [Fact]
        public void The_message_is_the_one_FluentEmail_SmtpSender_builds()
        {
            var bytes = Encoding.UTF8.GetBytes("attachment body");
            IFluentEmail Build() => new Email("sender@test.local", "Sender Name")
                .To("a@test.local", "A")
                .CC("c@test.local")
                .BCC("b@test.local")
                .ReplyTo("r@test.local")
                .Subject("Subject é")
                .Body("<p>Body é</p>", true)
                .HighPriority()
                .Header("X-Test", "1")
                .Attach(new FluentEmail.Core.Models.Attachment
                {
                    Data = new MemoryStream(bytes),
                    Filename = "file.txt",
                    ContentType = "text/plain"
                });

            using var ours = SmtpDelivery.CreateMailMessage(Build().Data);

            var create = typeof(FluentEmail.Smtp.SmtpSender).GetMethod("CreateMailMessage", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(create);
            using var theirs = (MailMessage)create.Invoke(new FluentEmail.Smtp.SmtpSender(new SmtpClient()), new object[] { Build() });

            Assert.Equal(theirs.From.ToString(), ours.From.ToString());
            Assert.Equal(theirs.Subject, ours.Subject);
            Assert.Equal(theirs.Body, ours.Body);
            Assert.Equal(theirs.IsBodyHtml, ours.IsBodyHtml);
            Assert.Equal(theirs.BodyEncoding, ours.BodyEncoding);
            Assert.Equal(theirs.SubjectEncoding, ours.SubjectEncoding);
            Assert.Equal(theirs.Priority, ours.Priority);
            Assert.Equal(theirs.To.ToString(), ours.To.ToString());
            Assert.Equal(theirs.CC.ToString(), ours.CC.ToString());
            Assert.Equal(theirs.Bcc.ToString(), ours.Bcc.ToString());
            Assert.Equal(theirs.ReplyToList.ToString(), ours.ReplyToList.ToString());
            Assert.Equal(theirs.Headers["X-Test"], ours.Headers["X-Test"]);
            Assert.Equal(theirs.Attachments.Select(a => a.Name + "|" + a.ContentType.MediaType),
                         ours.Attachments.Select(a => a.Name + "|" + a.ContentType.MediaType));
            Assert.Equal(theirs.AlternateViews.Count, ours.AlternateViews.Count);
        }

        internal static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                    Assert.Fail("condition not met within " + timeoutMs + " ms");
                await Task.Delay(25);
            }
        }
    }
}
