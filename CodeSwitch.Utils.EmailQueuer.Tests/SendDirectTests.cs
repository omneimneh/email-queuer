using CodeSwitch.Utils.EmailQueuer.Delivery;
using CodeSwitch.Utils.EmailQueuer.Extensions;
using FluentEmail.Core;
using FluentEmail.Core.Defaults;
using FluentEmail.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace CodeSwitch.Utils.EmailQueuer.Tests
{
    /// <summary>SendDirectAsync and inline (CID) attachments (2.5.0).</summary>
    public class SendDirectTests : IDisposable
    {
        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "eq-direct-" + Guid.NewGuid().ToString("N") + ".db");
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };

        private ServiceProvider BuildServices(int port, int sendTimeoutSeconds = 10, Action<IServiceCollection> extra = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddDbContext<QueueTestContext>(o => o.UseSqlite("Data Source=" + dbPath));
            services.AddEmailQueuer<QueueTestContext>(typeof(TestTemplates), o =>
            {
                o.SmtpClient.Host = "127.0.0.1";
                o.SmtpClient.Port = port;
                o.SmtpClient.EnableSsl = false;
                o.Sender = new NetworkCredential("sender@test.local", "x");
                o.TemplatePath = "/Mails/{0}.cshtml";
                o.PollIntervalSeconds = 0;
                o.SendTimeoutSeconds = sendTimeoutSeconds;
            });
            extra?.Invoke(services);
            var sp = services.BuildServiceProvider();
            using (var scope = sp.CreateScope())
                scope.ServiceProvider.GetRequiredService<QueueTestContext>().Database.EnsureCreated();
            return sp;
        }

        private static IFluentEmail Create(ServiceProvider sp) =>
            sp.GetRequiredService<IFluentEmailFactory>().Create();

        private static FluentEmail.Core.Models.Attachment Inline(string contentId) => new FluentEmail.Core.Models.Attachment
        {
            Data = new MemoryStream(Png, writable: false),
            Filename = "logo.png",
            ContentType = "image/png",
            ContentId = contentId,
            IsInline = true
        };

        private static FluentEmail.Core.Models.Attachment Regular() => new FluentEmail.Core.Models.Attachment
        {
            Data = new MemoryStream(Encoding.UTF8.GetBytes("report body")),
            Filename = "report.txt",
            ContentType = "text/plain"
        };

        private int QueueRows()
        {
            using var db = new QueueTestContext(new DbContextOptionsBuilder<QueueTestContext>().UseSqlite("Data Source=" + dbPath).Options);
            return db.EmailQueuerTasks.Count();
        }

        [Fact]
        public async Task SendDirectAsync_sends_now_through_the_configured_sender_without_a_queue_row()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port);
            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();

            var email = Create(sp).To("direct@test.local").Subject("Direct").Body("<p>Hi</p>", true);
            email.Header("List-Unsubscribe", "<https://example.test/u>");
            await queuer.SendDirectAsync(email);

            Assert.Equal(1, relay.Messages);
            Assert.Equal(new[] { "direct@test.local" }, relay.Recipients);
            var data = relay.MessageData.Single();
            Assert.Contains("From: sender@test.local", data);
            Assert.Contains("List-Unsubscribe: <https://example.test/u>", data);
            Assert.Equal(0, QueueRows());
            await SmtpDeliveryTests.WaitFor(() => relay.Quits == 1);
        }

        [Fact]
        public async Task SendDirectAsync_rejects_a_null_email()
        {
            await using var sp = BuildServices(25);
            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();

            await Assert.ThrowsAsync<ArgumentNullException>(() => queuer.SendDirectAsync(null));
        }

        [Fact]
        public async Task SendDirectAsync_is_bounded_by_the_send_timeout()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.SilentFromStart);
            await using var sp = BuildServices(relay.Port, sendTimeoutSeconds: 5);
            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();

            var sw = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                queuer.SendDirectAsync(Create(sp).To("silent@test.local").Subject("s").Body("b")));

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(12), $"took {sw.Elapsed}");
            Assert.Contains("did not complete within 5 seconds", error.Message);
            Assert.Equal(0, relay.Messages);
            Assert.Equal(0, QueueRows());
        }

        [Fact]
        public async Task SendDirectAsync_refuses_an_email_not_created_with_the_factory()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port);
            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();

            // new Email() gets FluentEmail's default sender, which would save the message to the root of the disk.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                queuer.SendDirectAsync(new Email("sender@test.local").To("a@test.local").Subject("s").Body("b")));

            Assert.Contains("IFluentEmailFactory", error.Message);
            Assert.Equal(0, relay.Connections);
        }

        [Fact]
        public async Task SendDirectAsync_refuses_an_email_without_a_sender()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port);
            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();
            var email = Create(sp).To("a@test.local").Subject("s").Body("b");
            email.Sender = null;

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => queuer.SendDirectAsync(email));

            Assert.Contains("IFluentEmailFactory", error.Message);
            Assert.Equal(0, relay.Connections);
        }

        [Fact]
        public async Task SendDirectAsync_accepts_a_factory_email_when_the_host_registered_the_save_to_disk_sender()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eq-disk-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                await using var sp = BuildServices(25, extra: s => s.Replace(ServiceDescriptor.Singleton<ISender>(new SaveToDiskSender(dir))));
                var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();

                await queuer.SendDirectAsync(Create(sp).To("a@test.local").Subject("Saved").Body("b"));

                Assert.Single(Directory.GetFiles(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task A_factory_email_shaped_like_a_release_announcement_is_sent_with_its_headers_and_inline_image()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port);
            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();
            var email = Create(sp)
                .To("member@test.local")
                .Subject("What's new")
                .Body("<p>New</p><img src=\"cid:shot1\">", true)
                .PlaintextAlternativeBody("New");
            email.Header("List-Unsubscribe", "<https://example.test/u/1>");
            email.Header("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
            email.Attach(Inline("shot1"));

            await queuer.SendDirectAsync(email);

            var data = relay.MessageData.Single();
            Assert.Contains("List-Unsubscribe: <https://example.test/u/1>", data);
            Assert.Contains("List-Unsubscribe-Post: List-Unsubscribe=One-Click", data);
            Assert.Contains("multipart/related", data);
            Assert.Contains("Content-ID: <shot1>", data);
            Assert.Contains("text/plain", data);
            Assert.Equal(0, QueueRows());
        }

        [Fact]
        public async Task SendDirectAsync_stops_when_its_token_is_cancelled()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.SilentFromStart);
            await using var sp = BuildServices(relay.Port, sendTimeoutSeconds: 60);
            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                queuer.SendDirectAsync(Create(sp).To("silent@test.local").Subject("s").Body("b"), cts.Token));

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
            Assert.Equal(0, relay.Messages);
        }

        [Fact]
        public async Task SendDirectAsync_with_a_cancelled_token_does_not_connect()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port);
            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                queuer.SendDirectAsync(Create(sp).To("a@test.local").Subject("s").Body("b"), new System.Threading.CancellationToken(true)));

            Assert.Equal(0, relay.Connections);
        }

        [Fact]
        public void A_content_id_with_a_line_break_is_refused()
        {
            var inline = new Email("sender@test.local").To("a@test.local").Subject("s").Body("<p>x</p>", true)
                .Attach(Inline("logo\r\nX-Injected: 1")).Data;
            var regular = Regular();
            regular.ContentId = "id\nX-Injected: 1";
            var attached = new Email("sender@test.local").To("a@test.local").Subject("s").Body("x").Attach(regular).Data;

            Assert.Throws<ArgumentException>(() => SmtpDelivery.CreateMailMessage(inline));
            Assert.Throws<ArgumentException>(() => SmtpDelivery.CreateMailMessage(attached));
        }

        [Fact]
        public void Several_inline_images_share_one_html_view_and_a_missing_content_type_is_octet_stream()
        {
            var untyped = Inline("second@test");
            untyped.ContentType = null;
            var data = new Email("sender@test.local").To("a@test.local").Subject("s")
                .Body("<img src=\"cid:first@test\"><img src=\"cid:second@test\">", true)
                .Attach(Inline("first@test")).Attach(untyped).Data;

            using var message = SmtpDelivery.CreateMailMessage(data);

            var view = Assert.Single(message.AlternateViews);
            Assert.Equal(new[] { "first@test", "second@test" }, view.LinkedResources.Select(r => r.ContentId).ToArray());
            Assert.Equal(new[] { "image/png", "application/octet-stream" }, view.LinkedResources.Select(r => r.ContentType.MediaType).ToArray());
            Assert.Empty(message.Attachments);
        }

        [Fact]
        public async Task An_inline_attachment_goes_out_as_a_linked_resource_of_the_html_body()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port);
            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();

            var email = Create(sp).To("cid@test.local").Subject("Inline")
                .Body("<p><img src=\"cid:logo@test\"></p>", true)
                .PlaintextAlternativeBody("Plain text");
            email.Attach(Inline("logo@test"));
            email.Attach(Regular());
            await queuer.SendDirectAsync(email);

            // multipart/mixed [ multipart/alternative [ text/plain, multipart/related [ text/html, image ] ], attachment ]
            var data = relay.MessageData.Single();
            var alternative = data.IndexOf("Content-Type: multipart/alternative", StringComparison.Ordinal);
            var plain = data.IndexOf("Content-Type: text/plain; charset", StringComparison.Ordinal);
            var related = data.IndexOf("Content-Type: multipart/related", StringComparison.Ordinal);
            var html = data.IndexOf("Content-Type: text/html", StringComparison.Ordinal);
            var image = data.IndexOf("Content-Type: image/png", StringComparison.Ordinal);
            var contentId = data.IndexOf("Content-ID: <logo@test>", StringComparison.Ordinal);
            var attachment = data.IndexOf("Content-Type: text/plain; name=report.txt", StringComparison.Ordinal);
            Assert.True(data.Contains("Content-Type: multipart/mixed") && alternative > 0, data);
            Assert.True(alternative < plain && plain < related && related < html && html < image && image < contentId && contentId < attachment, data);
            Assert.DoesNotContain("logo.png", data);
            Assert.Contains("Content-Disposition: attachment", data);
        }

        [Fact]
        public void Without_a_plain_text_body_the_html_becomes_an_alternate_view_that_carries_the_resource()
        {
            var data = new Email("sender@test.local").To("a@test.local").Subject("s")
                .Body("<img src=\"cid:logo@test\">", true)
                .Attach(Inline("logo@test")).Attach(Regular()).Data;

            using var message = SmtpDelivery.CreateMailMessage(data);

            var view = Assert.Single(message.AlternateViews);
            Assert.Equal("text/html", view.ContentType.MediaType);
            var resource = Assert.Single(view.LinkedResources);
            Assert.Equal("logo@test", resource.ContentId);
            Assert.Equal("image/png", resource.ContentType.MediaType);
            Assert.Equal(string.Empty, message.Body);
            Assert.False(message.IsBodyHtml);
            Assert.Equal("report.txt", Assert.Single(message.Attachments).Name);
        }

        [Fact]
        public void With_a_plain_text_body_the_existing_html_view_carries_the_resource()
        {
            var data = new Email("sender@test.local").To("a@test.local").Subject("s")
                .Body("<img src=\"cid:logo@test\">", true).PlaintextAlternativeBody("text")
                .Attach(Inline("logo@test")).Data;

            using var message = SmtpDelivery.CreateMailMessage(data);

            var view = Assert.Single(message.AlternateViews);
            Assert.Equal("text/html", view.ContentType.MediaType);
            Assert.Equal("logo@test", Assert.Single(view.LinkedResources).ContentId);
            Assert.Equal("text", message.Body);
            Assert.Empty(message.Attachments);
        }

        [Fact]
        public void An_inline_attachment_without_a_content_id_or_on_a_text_email_stays_a_normal_attachment()
        {
            var noContentId = new Email("sender@test.local").To("a@test.local").Subject("s")
                .Body("<p>x</p>", true).Attach(Inline(null)).Data;
            var textEmail = new Email("sender@test.local").To("a@test.local").Subject("s")
                .Body("x", false).Attach(Inline("logo@test")).Data;

            using var first = SmtpDelivery.CreateMailMessage(noContentId);
            using var second = SmtpDelivery.CreateMailMessage(textEmail);

            Assert.Empty(first.AlternateViews);
            Assert.Equal("logo.png", Assert.Single(first.Attachments).Name);
            Assert.True(first.IsBodyHtml);
            Assert.Empty(second.AlternateViews);
            Assert.Equal("logo@test", Assert.Single(second.Attachments).ContentId);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }
}
