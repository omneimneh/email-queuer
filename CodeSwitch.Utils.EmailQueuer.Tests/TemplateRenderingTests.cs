#pragma warning disable ASPDEPR006
using CodeSwitch.Utils.EmailQueuer.Extensions;
using CodeSwitch.Utils.EmailQueuer.Models;
using CodeSwitch.Utils.EmailQueuer.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CodeSwitch.Utils.EmailQueuer.Tests
{
    /// <summary>Razor rendering, CSS inlining and the context a queued render runs in (Razor.Templating.Core and PreMailer.Net, 2.5.0).</summary>
    public class TemplateRenderingTests : IDisposable
    {
        private readonly string dbPath = Path.Combine(Path.GetTempPath(), "eq-render-" + Guid.NewGuid().ToString("N") + ".db");

        private static readonly string Name = "Zo" + (char)0xEB + " <&> Co";

        // Captured from 2.4.0 (PreMailer.Net 2.2.0, Razor.Templating.Core 1.9.0) for the same template and model.
        private static readonly string InlinedOn240 = ""
            + "<!DOCTYPE html><html><head>\n"
            + "    <title>Styled</title>\n"
            + "    <style>\n"
            + "        p.lead { color: #112233; font-weight: bold; }\n"
            + "        .box { padding: 10px; border: 1px solid #000000; }\n"
            + "    </style>\n"
            + "</head>\n"
            + "<body>\n"
            + "    <div class=\"box\" style=\"padding: 10px;border: 1px solid #000000\"><style>\n"
            + "    .greeting { background-color: cornflowerblue; }\n"
            + "</style>\n"
            + "<p class=\"lead greeting\" style=\"background-color: cornflowerblue;color: #112233;font-weight: bold\">Hello Zo" + (char)0xEB + " &lt;&amp;&gt; Co</p>\n"
            + "<table><tbody><tr><td class=\"box\" style=\"padding: 10px;border: 1px solid #000000\">cell</td></tr></tbody></table>\n"
            + "<br>\n"
            + "<img src=\"cid:logo\" alt=\"logo\">\n"
            + "<!-- kept comment -->\n"
            + "<p>" + (char)0xA9 + " 2026 &amp; co</p>\n"
            + "</div>\n"
            + "\n"
            + "\n"
            + "</body></html>";

        private static EmailQueuer<QueueTestContext> Renderer(bool moveCssInline)
        {
            var options = EmailQueuerOptions.Default;
            options.TemplatePath = "/Mails/{0}.cshtml";
            options.MoveCssInline = moveCssInline;
            return new EmailQueuer<QueueTestContext>(null, options, NullLogger<EmailQueuer<QueueTestContext>>.Instance);
        }

        private static string Normalize(string html) => html.Replace("\r\n", "\n");

        [Fact]
        public async Task Css_is_moved_inline_exactly_as_with_PreMailer_2_2_0()
        {
            var html = await Renderer(moveCssInline: true).GenerateEmailBody("Styled", new PlainModel { Name = Name });

            Assert.Equal(InlinedOn240, Normalize(html));
        }

        [Fact]
        public async Task Without_MoveCssInline_the_style_blocks_stay_and_nothing_is_inlined()
        {
            var html = Normalize(await Renderer(moveCssInline: false).GenerateEmailBody("Styled", new PlainModel { Name = "x" }));

            Assert.Contains("<p class=\"lead greeting\">Hello x</p>", html);
            Assert.Contains("<div class=\"box\">", html);
            Assert.DoesNotContain("style=\"", html);
        }

        [Fact]
        public async Task A_queued_email_renders_after_the_request_that_queued_it_has_ended()
        {
            // The pass inherits the caller's ActionContext; Razor.Templating.Core 2.1.0 renders with it and fails here, 2.0.0 does not.
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port);
            using (var scope = sp.CreateScope())
                scope.ServiceProvider.GetRequiredService<QueueTestContext>().Database.EnsureCreated();
            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();

            var ended = new ServiceCollection().BuildServiceProvider();
            var requestScope = ended.CreateScope();
            var http = new DefaultHttpContext { RequestServices = requestScope.ServiceProvider };
            requestScope.Dispose();
            ended.Dispose();

            var accessor = new ActionContextAccessor();
            try
            {
                accessor.ActionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
                // Not started: the enqueue itself starts the pass, inside the caller's execution context.
                await queuer.EnqueueAsync("ambient@test.local", "Ambient", "Plain", new PlainModel { Name = "ambient" });
            }
            finally
            {
                accessor.ActionContext = null;
            }

            var row = await WaitForRow(r => r.Status != EmailTaskStatus.Pending && r.Status != EmailTaskStatus.Sending, 20);
            Assert.True(row.Status == EmailTaskStatus.Sent, $"status {row.Status}, error {row.LastError}");
            Assert.Equal(1, relay.Messages);
        }

        [Fact]
        public async Task The_queue_render_runs_in_the_callers_context_and_culture_as_in_2_4_0()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port);
            using (var scope = sp.CreateScope())
                scope.ServiceProvider.GetRequiredService<QueueTestContext>().Database.EnsureCreated();
            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();

            await EnqueueAsCaller(queuer, "first");

            var row = await WaitForRow(r => r.Status != EmailTaskStatus.Pending && r.Status != EmailTaskStatus.Sending, 20);
            Assert.True(row.Status == EmailTaskStatus.Sent, $"status {row.Status}, error {row.LastError}");
            Assert.Equal("ambient=caller|culture=fr-FR|ui=ar|n=1,5", RenderProbe.Seen("first"));
        }

        [Fact]
        public async Task A_retry_woken_by_the_timer_keeps_the_context_and_culture_of_the_pass_that_armed_it()
        {
            using var relay = new FakeSmtpServer(_ => RelayBehavior.Normal);
            await using var sp = BuildServices(relay.Port, retryBackoffSeconds: 1);
            using (var scope = sp.CreateScope())
                scope.ServiceProvider.GetRequiredService<QueueTestContext>().Database.EnsureCreated();
            var queuer = sp.GetRequiredService<EmailQueuer<QueueTestContext>>();
            RenderProbe.FailFirst("retried", 1);

            await EnqueueAsCaller(queuer, "retried");

            var row = await WaitForRow(r => r.Status == EmailTaskStatus.Sent || r.Status == EmailTaskStatus.PermanentlyFailed, 30);
            Assert.True(row.Status == EmailTaskStatus.Sent && row.RetryCount == 1, $"status {row.Status}, retries {row.RetryCount}, error {row.LastError}");
            Assert.Equal("ambient=caller|culture=fr-FR|ui=ar|n=1,5", RenderProbe.Seen("retried"));
        }

        private static async Task EnqueueAsCaller(EmailQueuer<QueueTestContext> queuer, string name)
        {
            var culture = CultureInfo.CurrentCulture;
            var uiCulture = CultureInfo.CurrentUICulture;
            try
            {
                RenderProbe.Ambient.Value = "caller";
                CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
                CultureInfo.CurrentUICulture = new CultureInfo("ar");
                // Not started: the enqueue itself starts the pass, inside the caller's execution context.
                await queuer.EnqueueAsync(name + "@test.local", "Probe", "Probe", new PlainModel { Name = name });
            }
            finally
            {
                RenderProbe.Ambient.Value = null;
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = uiCulture;
            }
        }

        private ServiceProvider BuildServices(int port, int retryBackoffSeconds = 30)
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
                o.SendTimeoutSeconds = 10;
                o.RetryBackoffSeconds = retryBackoffSeconds;
            });
            return services.BuildServiceProvider();
        }

        private async Task<EmailQueuerTask> WaitForRow(Func<EmailQueuerTask, bool> condition, int timeoutSeconds)
        {
            var sw = Stopwatch.StartNew();
            EmailQueuerTask row = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                using (var db = new QueueTestContext(new DbContextOptionsBuilder<QueueTestContext>().UseSqlite("Data Source=" + dbPath).Options))
                    row = await db.EmailQueuerTasks.AsNoTracking().SingleOrDefaultAsync();
                if (row != null && condition(row))
                    return row;
                await Task.Delay(100);
            }
            Assert.Fail($"row not in the expected state after {timeoutSeconds} s: {row?.Status} {row?.LastError}");
            return null;
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }

    /// <summary>What Mails/Probe.cshtml saw when it rendered, by model name, and planned render failures.</summary>
    public static class RenderProbe
    {
        public static readonly AsyncLocal<string> Ambient = new AsyncLocal<string>();
        private static readonly ConcurrentDictionary<string, string> seen = new ConcurrentDictionary<string, string>();
        private static readonly ConcurrentDictionary<string, int> failuresLeft = new ConcurrentDictionary<string, int>();

        public static void FailFirst(string name, int times) => failuresLeft[name] = times;

        public static string Seen(string name) => seen.TryGetValue(name, out var value) ? value : null;

        public static void Record(string name)
        {
            seen[name] = $"ambient={Ambient.Value}|culture={CultureInfo.CurrentCulture.Name}|ui={CultureInfo.CurrentUICulture.Name}|n={1.5m}";
            if (failuresLeft.TryGetValue(name, out var left) && left > 0)
            {
                failuresLeft[name] = left - 1;
                throw new InvalidOperationException("Planned render failure");
            }
        }
    }
}
