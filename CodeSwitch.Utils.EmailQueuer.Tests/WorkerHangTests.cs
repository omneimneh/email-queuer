using CodeSwitch.Utils.EmailQueuer.Extensions;
using CodeSwitch.Utils.EmailQueuer.Models;
using CodeSwitch.Utils.EmailQueuer.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CodeSwitch.Utils.EmailQueuer.Tests
{
    public class PlainModel
    {
        public string Name { get; set; }
    }

    /// <summary>Root of the test templates (Mails/Plain.cshtml); the model types live in this assembly.</summary>
    public class TestTemplates { }

    public class QueueTestContext : DbContext, IEmailQueuerContext
    {
        public QueueTestContext(DbContextOptions<QueueTestContext> options) : base(options) { }

        public DbSet<EmailQueuerTask> EmailQueuerTasks { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ((IEmailQueuerContext)this).Initialize(modelBuilder);
        }
    }

    /// <summary>
    /// The whole worker against a relay that accepts the first connection and never answers: the
    /// stalled email must fail within the send timeout, record why, wait its backoff, and must not
    /// hold up the email queued behind it.
    /// </summary>
    public class WorkerHangTests : IDisposable
    {
        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "eq-hang-" + Guid.NewGuid().ToString("N") + ".db");

        private ServiceProvider BuildServices(int port, int sendTimeoutSeconds)
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
                o.MaxRetryAttempts = 3;
                o.RetryBackoffSeconds = 1;
                o.PollIntervalSeconds = 0;
                o.StaleClaimMinutes = 10;
                o.SendTimeoutSeconds = sendTimeoutSeconds;
            });
            return services.BuildServiceProvider();
        }

        [Fact]
        public async Task A_hung_send_fails_after_the_send_timeout_and_does_not_hold_the_queue()
        {
            using var relay = new FakeSmtpServer(n => n == 1 ? RelayBehavior.SilentFromStart : RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port, sendTimeoutSeconds: 5);
            using (var scope = sp.CreateScope())
                scope.ServiceProvider.GetRequiredService<QueueTestContext>().Database.EnsureCreated();

            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();
            Assert.Equal(TimeSpan.FromSeconds(5), queuer.EffectiveSendTimeout());

            // Enqueue before the worker starts, so both rows are in the first pass in Id order.
            await queuer.EnqueueAsync("first@test.local", "First", "Plain", new PlainModel { Name = "first" });
            await queuer.EnqueueAsync("second@test.local", "Second", "Plain", new PlainModel { Name = "second" });

            var sw = Stopwatch.StartNew();
            using var stop = new CancellationTokenSource();
            await queuer.StartAsync(stop.Token);
            try
            {
                // The first email's send hangs on the silent relay. Within the send timeout (5 s) plus
                // slack, it is back in Error with the reason, and the second email has gone out.
                var (first, second) = await WaitForRows(r => r.first.Status == EmailTaskStatus.Error
                                                             && r.second.Status == EmailTaskStatus.Sent, 20);
                var failedAfter = sw.Elapsed;
                Assert.True(failedAfter < TimeSpan.FromSeconds(15), $"first attempt took {failedAfter}");
                Assert.Equal(1, first.RetryCount);
                Assert.StartsWith("TimeoutException", first.LastError);
                Assert.Contains("did not complete within 5 seconds", first.LastError);
                Assert.NotNull(first.LastErrorOn);
                // The retry is scheduled by the backoff (1 s here), not by the 10-minute claim deadline.
                Assert.True(first.NextAttemptOn <= first.LastErrorOn.Value.AddSeconds(2),
                    $"NextAttemptOn {first.NextAttemptOn:o} LastErrorOn {first.LastErrorOn:o}");
                Assert.Equal(0, second.RetryCount);

                // The retry goes out on a new connection (the timer wake is floored at 5 s).
                (first, _) = await WaitForRows(r => r.first.Status == EmailTaskStatus.Sent, 20);
                Assert.Equal(1, first.RetryCount);
                Assert.Null(first.LastError);
                Assert.Equal(2, relay.Messages);
                Assert.True(relay.Connections >= 2);
            }
            finally
            {
                stop.Cancel();
                await queuer.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task The_connection_is_closed_when_the_pass_ends_so_a_later_email_never_reuses_it()
        {
            // The dev signature: connection 1 delivers one email, then stays open and never answers
            // again (a NAT dropped it while the queue was idle). 2.3.0-rc.1 wrote the next MAIL FROM
            // onto it and hung. Now the pass that sent the first email closes it with QUIT, and the
            // next email, queued later, goes out on a new connection at once.
            using var relay = new FakeSmtpServer(n => n == 1 ? RelayBehavior.SilentAfterFirstMessage : RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port, sendTimeoutSeconds: 30);
            using (var scope = sp.CreateScope())
                scope.ServiceProvider.GetRequiredService<QueueTestContext>().Database.EnsureCreated();

            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();
            using var stop = new CancellationTokenSource();
            await queuer.StartAsync(stop.Token);
            try
            {
                await queuer.EnqueueAsync("first@test.local", "First", "Plain", new PlainModel { Name = "first" });
                await SmtpDeliveryTests.WaitFor(() => relay.Messages == 1, 10000);
                await SmtpDeliveryTests.WaitFor(() => relay.Quits == 1, 5000);

                var sw = Stopwatch.StartNew();
                await queuer.EnqueueAsync("second@test.local", "Second", "Plain", new PlainModel { Name = "second" });
                var (_, second) = await WaitForRows(r => r.second.Status == EmailTaskStatus.Sent, 10);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"second email took {sw.Elapsed}");
                Assert.Equal(0, second.RetryCount);
                Assert.Equal(2, relay.Connections);
            }
            finally
            {
                stop.Cancel();
                await queuer.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Characterization_FluentEmail_SmtpSender_ignores_cancellation_on_a_silent_relay()
        {
            // Why 2.3.0-rc.1's 8-minute cancellation never fired: FluentEmail.Smtp 3.0.2 drops the
            // token, and the async SmtpClient path has no Timeout. Kept as a guard: if a FluentEmail
            // upgrade ever honours the token, this test fails and the bypass can be reconsidered.
            using var relay = new FakeSmtpServer(_ => RelayBehavior.SilentFromStart);
            using var client = new System.Net.Mail.SmtpClient("127.0.0.1", relay.Port) { EnableSsl = false, Timeout = 1000 };
            var sender = new FluentEmail.Smtp.SmtpSender(client);
            var email = new FluentEmail.Core.Email("sender@test.local").To("a@test.local").Subject("s").Body("b");
            email.Sender = sender;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var send = email.SendAsync(cts.Token);
            var finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(4)));

            Assert.NotSame(send, finished); // still running 3 s after cancellation and 3 s past SmtpClient.Timeout
            client.SendAsyncCancel();       // release it
            await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(5)));
            _ = send.Exception;
        }

        private async Task<(EmailQueuerTask first, EmailQueuerTask second)> WaitForRows(
            Func<(EmailQueuerTask first, EmailQueuerTask second), bool> condition, int timeoutSeconds)
        {
            var sw = Stopwatch.StartNew();
            (EmailQueuerTask, EmailQueuerTask) rows = default;
            while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                using (var db = new QueueTestContext(new DbContextOptionsBuilder<QueueTestContext>().UseSqlite("Data Source=" + dbPath).Options))
                {
                    var all = await db.EmailQueuerTasks.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
                    if (all.Count == 2)
                    {
                        rows = (all[0], all[1]);
                        if (condition(rows))
                            return rows;
                    }
                }
                await Task.Delay(100);
            }
            Assert.Fail($"rows not in the expected state after {timeoutSeconds} s: first={Describe(rows.Item1)} second={Describe(rows.Item2)}");
            return default;
        }

        private static string Describe(EmailQueuerTask t) =>
            t == null ? "none" : $"{t.Status} retry={t.RetryCount} next={t.NextAttemptOn:o} err={t.LastError}";

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }
}
