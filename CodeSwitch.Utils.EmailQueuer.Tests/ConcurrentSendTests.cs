using CodeSwitch.Utils.EmailQueuer.Extensions;
using CodeSwitch.Utils.EmailQueuer.Models;
using CodeSwitch.Utils.EmailQueuer.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CodeSwitch.Utils.EmailQueuer.Tests
{
    /// <summary>
    /// MaxConcurrentSends (2.4.0) against the in-process SMTP sink on 127.0.0.1: nothing leaves the
    /// machine. Every test enqueues before the worker starts, so the first pass sees all its rows.
    /// </summary>
    public class ConcurrentSendTests : IDisposable
    {
        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "eq-conc-" + Guid.NewGuid().ToString("N") + ".db");

        private ServiceProvider BuildServices(int port, int concurrency, int outagePauseThreshold = 0, int retryBackoffSeconds = 1)
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
                o.RetryBackoffSeconds = retryBackoffSeconds;
                o.OutagePauseThreshold = outagePauseThreshold;
                o.PollIntervalSeconds = 0;
                o.StaleClaimMinutes = 10;
                o.SendTimeoutSeconds = 10;
                o.MaxConcurrentSends = concurrency;
            });
            return services.BuildServiceProvider();
        }

        private static EmailQueuer<QueueTestContext> CreateDatabaseAndQueuer(ServiceProvider sp)
        {
            using (var scope = sp.CreateScope())
                scope.ServiceProvider.GetRequiredService<QueueTestContext>().Database.EnsureCreated();
            return sp.GetRequiredService<EmailQueuer<QueueTestContext>>();
        }

        /// <summary>
        /// Writes the row the way EnqueueAsync does, without its wake: EnqueueAsync starts a pass at
        /// once, even before StartAsync, and the first rows would then be sent before the later ones
        /// exist. Inserting directly makes the first pass see every row, so the order is deterministic.
        /// </summary>
        private async Task Enqueue(EmailQueuer<QueueTestContext> queuer, string to, int priority = 0, string partition = null)
        {
            using var db = new QueueTestContext(new DbContextOptionsBuilder<QueueTestContext>().UseSqlite("Data Source=" + dbPath).Options);
            db.EmailQueuerTasks.Add(new EmailQueuerTask
            {
                To = to,
                CC = "",
                BCC = "",
                AttachmentPaths = "",
                Subject = "Subject " + to,
                Template = "Plain",
                CreatedOn = DateTime.UtcNow,
                ModelType = typeof(PlainModel).FullName,
                ModelJson = Newtonsoft.Json.JsonConvert.SerializeObject(new PlainModel { Name = to }),
                Status = EmailTaskStatus.Pending,
                Priority = priority,
                PartitionKey = partition
            });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task Four_senders_send_at_once_never_more_and_each_email_exactly_once()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal) { DataDelay = TimeSpan.FromMilliseconds(600) };
            await using var sp = BuildServices(relay.Port, concurrency: 4);
            var queuer = CreateDatabaseAndQueuer(sp);
            Assert.Equal(4, queuer.EffectiveConcurrency());

            for (var i = 0; i < 12; i++)
                await Enqueue(queuer, $"r{i:00}@test.local");

            var sw = Stopwatch.StartNew();
            using var stop = new CancellationTokenSource();
            await queuer.StartAsync(stop.Token);
            try
            {
                var rows = await WaitForRows(all => all.All(r => r.Status == EmailTaskStatus.Sent), 12, 30);
                var elapsed = sw.Elapsed;

                Assert.Equal(4, relay.PeakConcurrentData);
                Assert.Equal(12, relay.Messages);
                Assert.Equal(12, relay.Recipients.Distinct().Count());
                Assert.All(rows, r => Assert.Equal(0, r.RetryCount));
                Assert.All(rows, r => Assert.Null(r.LastError));
                // One at a time the relay alone needs 12 x 0.6 = 7.2 s.
                Assert.True(elapsed < TimeSpan.FromSeconds(7), $"drain took {elapsed}");
            }
            finally
            {
                stop.Cancel();
                await queuer.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task The_default_of_one_sends_one_email_at_a_time()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal) { DataDelay = TimeSpan.FromMilliseconds(200) };
            await using var sp = BuildServices(relay.Port, concurrency: 0); // 0 or less means 1
            var queuer = CreateDatabaseAndQueuer(sp);
            Assert.Equal(1, queuer.EffectiveConcurrency());

            for (var i = 0; i < 4; i++)
                await Enqueue(queuer, $"s{i}@test.local");

            using var stop = new CancellationTokenSource();
            await queuer.StartAsync(stop.Token);
            try
            {
                await WaitForRows(all => all.All(r => r.Status == EmailTaskStatus.Sent), 4, 20);
                Assert.Equal(1, relay.PeakConcurrentData);
                Assert.Equal(4, relay.Messages);
                // One connection for the pass, as in 2.3.0.
                Assert.Equal(1, relay.Connections);
                Assert.Equal(new[] { "s0@test.local", "s1@test.local", "s2@test.local", "s3@test.local" }, relay.Recipients);
            }
            finally
            {
                stop.Cancel();
                await queuer.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Higher_priority_mail_is_all_sent_before_any_lower_priority_mail_starts()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal) { DataDelay = TimeSpan.FromMilliseconds(300) };
            await using var sp = BuildServices(relay.Port, concurrency: 4);
            var queuer = CreateDatabaseAndQueuer(sp);

            // The bulk mail is older (lower ids) than the transactional mail.
            for (var i = 0; i < 8; i++)
                await Enqueue(queuer, $"bulk{i}@test.local", priority: 0, partition: "tournament");
            for (var i = 0; i < 3; i++)
                await Enqueue(queuer, $"urgent{i}@test.local", priority: 10, partition: "reset");

            using var stop = new CancellationTokenSource();
            await queuer.StartAsync(stop.Token);
            try
            {
                var rows = await WaitForRows(all => all.All(r => r.Status == EmailTaskStatus.Sent), 11, 30);
                var urgentLast = rows.Where(r => r.Priority == 10).Max(r => r.SentOn);
                var bulkFirst = rows.Where(r => r.Priority == 0).Min(r => r.SentOn);
                Assert.True(urgentLast <= bulkFirst, $"last urgent {urgentLast:o}, first bulk {bulkFirst:o}");
                // No bulk email even reached the relay before every urgent one had.
                Assert.All(relay.Recipients.Take(3), r => Assert.StartsWith("urgent", r));
                Assert.Equal(11, relay.Recipients.Distinct().Count());
            }
            finally
            {
                stop.Cancel();
                await queuer.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Partitions_still_take_turns_so_a_small_partition_is_not_stuck_behind_a_burst()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal) { DataDelay = TimeSpan.FromMilliseconds(300) };
            await using var sp = BuildServices(relay.Port, concurrency: 2);
            var queuer = CreateDatabaseAndQueuer(sp);

            for (var i = 0; i < 9; i++)
                await Enqueue(queuer, $"a{i}@test.local", partition: "company-a");
            await Enqueue(queuer, "b0@test.local", partition: "company-b");
            await Enqueue(queuer, "c0@test.local", partition: "company-c");

            using var stop = new CancellationTokenSource();
            await queuer.StartAsync(stop.Token);
            try
            {
                await WaitForRows(all => all.All(r => r.Status == EmailTaskStatus.Sent), 11, 30);
                // The first round is one head per partition: B and C go out with A's first email,
                // not after A's nine.
                var firstRound = relay.Recipients.Take(3).ToList();
                Assert.Contains("b0@test.local", firstRound);
                Assert.Contains("c0@test.local", firstRound);
                Assert.Equal(2, relay.PeakConcurrentData);
            }
            finally
            {
                stop.Cancel();
                await queuer.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task Two_workers_with_four_senders_each_never_send_an_email_twice()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal) { DataDelay = TimeSpan.FromMilliseconds(100) };
            await using var sp1 = BuildServices(relay.Port, concurrency: 4);
            await using var sp2 = BuildServices(relay.Port, concurrency: 4);
            var q1 = CreateDatabaseAndQueuer(sp1);
            var q2 = sp2.GetRequiredService<EmailQueuer<QueueTestContext>>();

            for (var i = 0; i < 24; i++)
                await Enqueue(q1, $"d{i:00}@test.local");

            using var stop = new CancellationTokenSource();
            await Task.WhenAll(q1.StartAsync(stop.Token), q2.StartAsync(stop.Token));
            try
            {
                var rows = await WaitForRows(all => all.All(r => r.Status == EmailTaskStatus.Sent), 24, 60);
                // Give a double send time to show up.
                await Task.Delay(1000);
                Assert.Equal(24, relay.Messages);
                Assert.Equal(24, relay.Recipients.Length);
                Assert.Equal(24, relay.Recipients.Distinct().Count());
                Assert.All(rows, r => Assert.Equal(0, r.RetryCount));
                Assert.True(relay.PeakConcurrentData <= 8, $"peak {relay.PeakConcurrentData}");
            }
            finally
            {
                stop.Cancel();
                await Task.WhenAll(q1.StopAsync(CancellationToken.None), q2.StopAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task An_outage_stops_new_sends_and_the_emails_not_attempted_keep_their_whole_budget()
        {
            // A port nobody listens on: every connection is refused, a transport failure.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            // Pause after 2 different emails failed; the pause (30 s) outlasts the test's look.
            await using var sp = BuildServices(deadPort, concurrency: 4, outagePauseThreshold: 2, retryBackoffSeconds: 30);
            var queuer = CreateDatabaseAndQueuer(sp);
            for (var i = 0; i < 16; i++)
                await Enqueue(queuer, $"o{i:00}@test.local");

            using var stop = new CancellationTokenSource();
            await queuer.StartAsync(stop.Token);
            try
            {
                await WaitForRows(all => all.Count(r => r.Status == EmailTaskStatus.Error) >= 2, 16, 30);
                // Let the sends that were already under way end.
                await Task.Delay(TimeSpan.FromSeconds(12));
                var rows = await ReadRows();

                var attempted = rows.Where(r => r.RetryCount > 0).ToList();
                var untouched = rows.Where(r => r.RetryCount == 0).ToList();
                Assert.InRange(attempted.Count, 2, 8);
                Assert.All(attempted, r => Assert.Equal(1, r.RetryCount));
                Assert.All(attempted, r => Assert.Equal(EmailTaskStatus.Error, r.Status));
                Assert.All(untouched, r => Assert.Equal(EmailTaskStatus.Pending, r.Status));
                Assert.True(untouched.Count >= 8, $"only {untouched.Count} emails left untouched");
                Assert.DoesNotContain(rows, r => r.Status == EmailTaskStatus.Sending);
            }
            finally
            {
                stop.Cancel();
                await queuer.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public void MaxConcurrentSends_binds_from_configuration_and_is_one_when_absent()
        {
            EmailQueuerOptions Load(Dictionary<string, string> extra)
            {
                var values = new Dictionary<string, string>
                {
                    ["EmailQueuer:Sender:Email"] = "sender@test.local",
                    ["EmailQueuer:Sender:Password"] = "x",
                    ["EmailQueuer:Smtp:Host"] = "127.0.0.1",
                    ["EmailQueuer:Smtp:Port"] = "25",
                    ["EmailQueuer:TemplatePath"] = "{0}.cshtml",
                };
                foreach (var kv in extra) values[kv.Key] = kv.Value;
                var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
                var o = EmailQueuerOptions.Default;
                o.LoadFromConfiguration(config);
                return o;
            }

            Assert.Equal(1, Load(new Dictionary<string, string>()).MaxConcurrentSends);
            Assert.Equal(4, Load(new Dictionary<string, string> { ["EmailQueuer:MaxConcurrentSends"] = "4" }).MaxConcurrentSends);
            Assert.Equal(1, Load(new Dictionary<string, string> { ["EmailQueuer:MaxConcurrentSends"] = "0" }).MaxConcurrentSends);

            var big = new EmailQueuer<QueueTestContext>(null, new EmailQueuerOptions { MaxConcurrentSends = 500 }, null);
            Assert.Equal(EmailQueuerOptions.MaxConcurrentSendsCeiling, big.EffectiveConcurrency());
        }

        private async Task<List<EmailQueuerTask>> ReadRows()
        {
            using var db = new QueueTestContext(new DbContextOptionsBuilder<QueueTestContext>().UseSqlite("Data Source=" + dbPath).Options);
            return await db.EmailQueuerTasks.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        }

        private async Task<List<EmailQueuerTask>> WaitForRows(Func<List<EmailQueuerTask>, bool> condition, int expectedCount, int timeoutSeconds)
        {
            var sw = Stopwatch.StartNew();
            List<EmailQueuerTask> rows = new List<EmailQueuerTask>();
            while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                rows = await ReadRows();
                if (rows.Count == expectedCount && condition(rows))
                    return rows;
                await Task.Delay(100);
            }
            Assert.Fail($"rows not in the expected state after {timeoutSeconds} s: "
                        + string.Join(", ", rows.Select(t => $"{t.Id}:{t.Status}/r{t.RetryCount}")));
            return rows;
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }
}
