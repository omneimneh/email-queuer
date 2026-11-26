using CodeSwitch.Utils.EmailQueuer.Extensions;
using CodeSwitch.Utils.EmailQueuer.Models;
using CodeSwitch.Utils.EmailQueuer.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CodeSwitch.Utils.EmailQueuer.Tests
{
    public class SecretModel
    {
        public string Name { get; set; }
        public string Password { get; set; }
    }

    /// <summary>The README's example scrubber, as written there: a public static method assigned as a method group.</summary>
    public static class MailModelScrubber
    {
        public static string Scrub(string template, string modelJson)
        {
            var reader = new JsonTextReader(new StringReader(modelJson)) { DateParseHandling = DateParseHandling.None };
            if (JToken.ReadFrom(reader) is not JObject model || !model.Remove("Password"))
                return modelJson;
            return model.ToString(Formatting.None);
        }
    }

    /// <summary>
    /// EmailQueuerOptions.FinishedModelScrubber (2.5.0) against the in-process SMTP sink and an SQLite file.
    /// Rows are inserted before the worker starts and always read back through a new context.
    /// </summary>
    public class FinishedModelScrubberTests : IDisposable
    {
        private const string Password = "S3cret-Pa55";
        private static readonly string Original = "{\"Name\":\"Ann\",\"Password\":\"" + Password + "\"}";

        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "eq-scrub-" + Guid.NewGuid().ToString("N") + ".db");
        private readonly UpdateRecorder updates = new UpdateRecorder();
        private readonly CapturingLoggerProvider logs = new CapturingLoggerProvider();
        private readonly ConcurrentQueue<(string Template, string ModelJson)> calls = new ConcurrentQueue<(string, string)>();

        internal static string RemovePassword(string modelJson)
        {
            var model = JObject.Parse(modelJson);
            model.Remove("Password");
            return model.ToString(Formatting.None);
        }

        private string RecordingScrubber(string template, string modelJson)
        {
            calls.Enqueue((template, modelJson));
            return RemovePassword(modelJson);
        }

        [Fact]
        public async Task Without_a_scrubber_the_sent_row_keeps_its_model_byte_for_byte()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port, scrubber: null);
            var row = await RunOneAsync(sp, r => r.Status == EmailTaskStatus.Sent);

            Assert.Equal(Original, row.ModelJson);
            Assert.False(SentUpdateWritesModel());
        }

        [Fact]
        public async Task A_sent_row_is_saved_without_the_removed_key_in_the_update_that_records_Sent()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port, RecordingScrubber);
            var row = await RunOneAsync(sp, r => r.Status == EmailTaskStatus.Sent);

            Assert.Equal("{\"Name\":\"Ann\"}", row.ModelJson);
            var call = Assert.Single(calls);
            Assert.Equal("Secret", call.Template);
            Assert.Equal(Original, call.ModelJson);

            // One UPDATE records Sent and the scrubbed model; no other command writes the model.
            Assert.True(SentUpdateWritesModel());
            Assert.Single(updates.Texts, t => t.Contains("\"ModelJson\""));
        }

        [Fact]
        public async Task The_email_is_rendered_from_the_full_model_before_the_scrub()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port, RecordingScrubber);
            var row = await RunOneAsync(sp, r => r.Status == EmailTaskStatus.Sent);

            Assert.Contains("your starting password is " + Password, DecodedBody(relay.MessageData.Single()));
            Assert.DoesNotContain(Password, row.ModelJson);
        }

        [Fact]
        public async Task A_row_that_fails_its_last_attempt_is_PermanentlyFailed_and_scrubbed()
        {
            await using var sp = BuildServices(DeadPort(), RecordingScrubber, maxRetryAttempts: 0);
            var row = await RunOneAsync(sp, r => r.Status == EmailTaskStatus.PermanentlyFailed);

            Assert.Equal(1, row.RetryCount);
            Assert.NotNull(row.LastError);
            Assert.Equal("{\"Name\":\"Ann\"}", row.ModelJson);
            Assert.Single(calls);
        }

        [Fact]
        public async Task A_row_that_will_be_retried_keeps_its_model_and_the_scrubber_is_not_called()
        {
            await using var sp = BuildServices(DeadPort(), RecordingScrubber, maxRetryAttempts: 3);
            var row = await RunOneAsync(sp, r => r.Status == EmailTaskStatus.Error);

            Assert.Equal(1, row.RetryCount);
            Assert.Equal(Original, row.ModelJson);
            Assert.Empty(calls);
        }

        [Fact]
        public async Task A_scrubber_that_throws_keeps_the_model_still_records_Sent_and_logs_the_type_only()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port, (template, json) =>
            {
                calls.Enqueue((template, json));
                throw new InvalidOperationException("secret-value " + json);
            });
            var startedOn = DateTime.UtcNow.AddSeconds(-1);
            var row = await RunOneAsync(sp, r => r.Status != EmailTaskStatus.Pending && r.Status != EmailTaskStatus.Sending);

            Assert.Equal(EmailTaskStatus.Sent, row.Status);
            Assert.True(row.SentOn >= startedOn, $"SentOn {row.SentOn:o}");
            Assert.Equal(Original, row.ModelJson);
            Assert.Single(calls);
            await Task.Delay(500);
            Assert.Equal(1, relay.Messages);

            var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("scrubber"));
            Assert.Contains(nameof(InvalidOperationException), warning.Message);
            Assert.Null(warning.Exception);
            Assert.DoesNotContain(logs.Entries, e => e.Text.Contains("secret-value") || e.Text.Contains(Password));
        }

        [Fact]
        public async Task A_scrubber_that_returns_null_keeps_the_model()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port, (template, json) =>
            {
                calls.Enqueue((template, json));
                return null;
            });
            var row = await RunOneAsync(sp, r => r.Status == EmailTaskStatus.Sent);

            Assert.Equal(Original, row.ModelJson);
            Assert.Single(calls);
            Assert.False(SentUpdateWritesModel());
        }

        [Fact]
        public async Task A_scrubber_that_returns_its_input_keeps_the_model_and_the_update_does_not_write_it()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port, (template, json) =>
            {
                calls.Enqueue((template, json));
                return new string(json.ToCharArray());
            });
            var row = await RunOneAsync(sp, r => r.Status == EmailTaskStatus.Sent);

            Assert.Equal(Original, row.ModelJson);
            Assert.Single(calls);
            Assert.False(SentUpdateWritesModel());
            Assert.DoesNotContain(updates.Texts, t => t.Contains("\"ModelJson\""));
        }

        [Fact]
        public async Task With_three_concurrent_senders_every_row_is_scrubbed_exactly_once()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal) { DataDelay = TimeSpan.FromMilliseconds(300) };
            await using var sp = BuildServices(relay.Port, RecordingScrubber, concurrency: 3);
            var queuer = CreateDatabaseAndQueuer(sp);
            for (var i = 0; i < 9; i++)
                await InsertAsync(new SecretModel { Name = "user" + i, Password = Password + i }, $"user{i}@test.local");

            var rows = await RunAsync(queuer, all => all.Count == 9 && all.All(r => r.Status == EmailTaskStatus.Sent));

            Assert.Equal(3, relay.PeakConcurrentData);
            Assert.Equal(9, relay.Messages);
            Assert.All(rows, r => Assert.DoesNotContain("Password", r.ModelJson));
            Assert.Equal(rows.Select(r => r.To.Replace("@test.local", "")).OrderBy(x => x),
                rows.Select(r => JObject.Parse(r.ModelJson)["Name"].ToString()).OrderBy(x => x));
            Assert.Equal(9, calls.Count);
            Assert.Equal(9, calls.Select(c => c.ModelJson).Distinct().Count());
        }

        [Fact]
        public async Task A_public_static_method_can_be_assigned_as_the_scrubber()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port, null, configure: o => o.FinishedModelScrubber = MailModelScrubber.Scrub);
            var row = await RunOneAsync(sp, r => r.Status == EmailTaskStatus.Sent);

            Assert.Equal("{\"Name\":\"Ann\"}", row.ModelJson);
            Assert.Equal(1, relay.Messages);
        }

        [Fact]
        public void The_README_scrubber_keeps_dates_as_written_and_leaves_a_model_without_the_key_untouched()
        {
            const string withDate = "{\"Name\":\"Ann\",\"Password\":\"x\",\"On\":\"2026-11-26T10:00:00.1234567+03:00\"}";
            Assert.Equal("{\"Name\":\"Ann\",\"On\":\"2026-11-26T10:00:00.1234567+03:00\"}", MailModelScrubber.Scrub("Secret", withDate));

            const string without = "{\"Name\":\"Ann\"}";
            Assert.Same(without, MailModelScrubber.Scrub("Secret", without));
            Assert.Same("[1,2]", MailModelScrubber.Scrub("Secret", "[1,2]"));
        }

        [Fact]
        public async Task A_row_the_stale_claim_reclaim_retires_is_not_passed_to_the_scrubber()
        {
            await using var sp = BuildServices(DeadPort(), RecordingScrubber, maxRetryAttempts: 3);
            var queuer = CreateDatabaseAndQueuer(sp);
            await InsertAsync(new SecretModel { Name = "Ann", Password = Password }, "ann@test.local", Original,
                EmailTaskStatus.Sending, retryCount: 3, nextAttemptOn: DateTime.UtcNow.AddMinutes(-1));

            var rows = await RunAsync(queuer, all => all.Single().Status == EmailTaskStatus.PermanentlyFailed);

            Assert.Equal(Original, rows.Single().ModelJson);
            Assert.Empty(calls);
        }

        private ServiceProvider BuildServices(int port, Func<string, string, string> scrubber, int maxRetryAttempts = 3,
            int concurrency = 1, Action<EmailQueuerOptions> configure = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddDbContext<QueueTestContext>(o => o.UseSqlite("Data Source=" + dbPath).AddInterceptors(updates));
            services.AddEmailQueuer<QueueTestContext>(typeof(TestTemplates), o =>
            {
                o.SmtpClient.Host = "127.0.0.1";
                o.SmtpClient.Port = port;
                o.SmtpClient.EnableSsl = false;
                o.Sender = new NetworkCredential("sender@test.local", "x");
                o.TemplatePath = "/Mails/{0}.cshtml";
                o.MaxRetryAttempts = maxRetryAttempts;
                o.RetryBackoffSeconds = 30;
                o.PollIntervalSeconds = 0;
                o.StaleClaimMinutes = 10;
                o.SendTimeoutSeconds = 10;
                o.MaxConcurrentSends = concurrency;
                o.FinishedModelScrubber = scrubber;
                configure?.Invoke(o);
            });
            return services.BuildServiceProvider();
        }

        private static EmailQueuer<QueueTestContext> CreateDatabaseAndQueuer(ServiceProvider sp)
        {
            using (var scope = sp.CreateScope())
                scope.ServiceProvider.GetRequiredService<QueueTestContext>().Database.EnsureCreated();
            return sp.GetRequiredService<EmailQueuer<QueueTestContext>>();
        }

        private async Task<EmailQueuerTask> RunOneAsync(ServiceProvider sp, Func<EmailQueuerTask, bool> done)
        {
            var queuer = CreateDatabaseAndQueuer(sp);
            await InsertAsync(new SecretModel { Name = "Ann", Password = Password }, "ann@test.local", Original);
            var rows = await RunAsync(queuer, all => all.Count == 1 && done(all[0]));
            return rows.Single();
        }

        private async Task<List<EmailQueuerTask>> RunAsync(EmailQueuer<QueueTestContext> queuer, Func<List<EmailQueuerTask>, bool> done)
        {
            using var stop = new CancellationTokenSource();
            await queuer.StartAsync(stop.Token);
            try
            {
                var sw = Stopwatch.StartNew();
                var rows = new List<EmailQueuerTask>();
                while (sw.Elapsed < TimeSpan.FromSeconds(30))
                {
                    rows = await ReadRowsAsync();
                    if (rows.Count > 0 && done(rows))
                        return rows;
                    await Task.Delay(100);
                }
                Assert.Fail("rows not in the expected state: " + string.Join(", ", rows.Select(r => $"{r.Id}:{r.Status}/r{r.RetryCount} {r.LastError}")));
                return rows;
            }
            finally
            {
                stop.Cancel();
                await queuer.StopAsync(CancellationToken.None);
            }
        }

        /// <summary>Writes the row the way EnqueueAsync does, without starting a pass.</summary>
        private async Task InsertAsync(SecretModel model, string to, string modelJson = null,
            EmailTaskStatus status = EmailTaskStatus.Pending, int retryCount = 0, DateTime? nextAttemptOn = null)
        {
            using var db = NewContext();
            db.EmailQueuerTasks.Add(new EmailQueuerTask
            {
                To = to,
                CC = "",
                BCC = "",
                AttachmentPaths = "",
                Subject = "Welcome",
                Template = "Secret",
                CreatedOn = DateTime.UtcNow,
                ModelType = typeof(SecretModel).FullName,
                ModelJson = modelJson ?? JsonConvert.SerializeObject(model),
                Status = status,
                RetryCount = retryCount,
                NextAttemptOn = nextAttemptOn
            });
            await db.SaveChangesAsync();
        }

        private async Task<List<EmailQueuerTask>> ReadRowsAsync()
        {
            using var db = NewContext();
            return await db.EmailQueuerTasks.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        }

        private QueueTestContext NewContext() =>
            new QueueTestContext(new DbContextOptionsBuilder<QueueTestContext>().UseSqlite("Data Source=" + dbPath).Options);

        /// <summary>Whether the single UPDATE that recorded Sent also wrote the model.</summary>
        private bool SentUpdateWritesModel()
        {
            var text = Assert.Single(updates.Texts, t => t.Contains("\"SentOn\""));
            return text.Contains("\"ModelJson\"");
        }

        private static int DeadPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>The HTML part of a single-part message, which System.Net.Mail sends base64-encoded for UTF-8.</summary>
        private static string DecodedBody(string data)
        {
            var encoding = data.IndexOf("Content-Transfer-Encoding: base64", StringComparison.OrdinalIgnoreCase);
            Assert.True(encoding >= 0, "the body is not base64-encoded");
            var start = data.IndexOf("\r\n\r\n", encoding, StringComparison.Ordinal) + 4;
            return Encoding.UTF8.GetString(Convert.FromBase64String(data.Substring(start).Replace("\r\n", "").Trim()));
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }

    /// <summary>The text of every UPDATE command EF Core runs.</summary>
    public sealed class UpdateRecorder : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> texts = new ConcurrentQueue<string>();

        public string[] Texts => texts.ToArray();

        private void Record(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                texts.Enqueue(command.CommandText);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return new ValueTask<InterceptionResult<int>>(result);
        }
    }

    /// <summary>Keeps every log entry with its formatted message and exception.</summary>
    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public sealed class Entry
        {
            public LogLevel Level { get; set; }
            public string Category { get; set; }
            public string Message { get; set; }
            public Exception Exception { get; set; }
            public string Text => Message + " " + Exception;
        }

        private readonly ConcurrentQueue<Entry> entries = new ConcurrentQueue<Entry>();

        public Entry[] Entries => entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose() { }

        private sealed class Logger : ILogger
        {
            private readonly CapturingLoggerProvider provider;
            private readonly string category;

            public Logger(CapturingLoggerProvider provider, string category)
            {
                this.provider = provider;
                this.category = category;
            }

            public IDisposable BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                provider.entries.Enqueue(new Entry { Level = logLevel, Category = category, Message = formatter(state, exception), Exception = exception });
            }
        }
    }
}
