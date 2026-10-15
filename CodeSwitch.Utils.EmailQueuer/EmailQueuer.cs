using CodeSwitch.Utils.EmailQueuer.Delivery;
using CodeSwitch.Utils.EmailQueuer.Models;
using CodeSwitch.Utils.EmailQueuer.Options;
using FluentEmail.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Razor.Templating.Core;

namespace CodeSwitch.Utils.EmailQueuer
{
    public class EmailQueuer<TContext> : BackgroundService where TContext : DbContext, IEmailQueuerContext
    {
        /// <summary>Rows loaded (and at most sent) between two reads of the queue head.</summary>
        private const int ClaimBatchSize = 10;
        /// <summary>Floor on the periodic wake, so a misconfiguration cannot turn it into a hot poll.</summary>
        private const int MinPollIntervalSeconds = 30;
        /// <summary>Wait before the next pass after a pass died on an exception (a SQL error for instance).</summary>
        private const int PassFailureRetrySeconds = 120;
        /// <summary>Floor on the send timeout, whatever the configuration says.</summary>
        private const int MinSendTimeoutSeconds = 5;
        /// <summary>
        /// An SMTP connection idle longer than this is closed (QUIT) and a new one opened for the
        /// next send. Far below the 4-minute idle timeout of Azure SNAT and NAT gateways, past which
        /// a connection is dropped silently and the next command on it never gets an answer.
        /// </summary>
        internal static readonly TimeSpan MaxSmtpConnectionIdle = TimeSpan.FromSeconds(30);
        /// <summary>Messages sent on one SMTP connection before it is replaced by a new one.</summary>
        internal const int MaxMessagesPerSmtpConnection = 50;
        private const int MaxTrackedPartitions = 1024;
        /// <summary>Ids remembered as "already failed in this outage". Past it new ids are counted but not remembered.</summary>
        private const int MaxTrackedOutageRows = 1024;
        /// <summary>The pause length stops growing long before this step; the cap keeps the step counter bounded.</summary>
        private const int MaxOutagePauseStep = 64;
        /// <summary>Floor on how long outage state is remembered after its last sign (see ForgetStaleOutage).</summary>
        private static readonly TimeSpan MinOutageMemory = TimeSpan.FromMinutes(5);
        /// <summary>Lane of the rows that carry no partition key (and of every row when fairness is off).</summary>
        private const string NullPartitionLane = "\u0000<none>";
        /// <summary>Floor on every timed wake: whatever the data says, a timer never re-runs a pass sooner.</summary>
        private static readonly TimeSpan MinWakeDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaxWakeDelay = TimeSpan.FromDays(1);
        /// <summary>Wait after a round in which every claim was lost to another worker instance.</summary>
        private static readonly TimeSpan LostRoundDelay = TimeSpan.FromSeconds(30);

        private readonly IServiceScopeFactory factory;
        private readonly EmailQueuerOptions options;
        private readonly ILogger<EmailQueuer<TContext>> logger;

        // Pass gate. passRunning is 1 while a drain owns the queue (and the shared SmtpClient);
        // passRequested is set by every wake so a request that arrives mid-pass is never lost.
        private int passRunning;
        private int passRequested;
        private CancellationToken hostStopping = CancellationToken.None;

        // One timer for every timed wake (next retry due, end of an outage pause, retry after a
        // failed pass). Arming replaces the previous due time.
        private readonly object wakeLock = new object();
        private Timer wakeTimer;
        private bool disposed;

        // Outage state. Written only by the running pass.
        // outageFailedRows counts DIFFERENT emails whose send failed on transport since the last
        // successful send (outageRowIds remembers which). One email that keeps failing is counted
        // once, so a single message the server refuses for its own content can never declare an
        // outage, however many times it is retried.
        private int outageFailedRows;
        private readonly HashSet<int> outageRowIds = new HashSet<int>();
        private int outagePauseStep;
        private long pausedUntilTicks;
        private long lastTransportFailureTicks;

        // Fairness state: when each partition lane was last served. Touched only by the running pass.
        private readonly Dictionary<string, long> lastServed = new Dictionary<string, long>();
        private long serveSequence;

        private enum RowOutcome { NotClaimed, Sent, Failed, Paused }

        private sealed class Lane
        {
            public string LaneKey;
            public string PartitionKey;
            public int HeadId;
        }

        public EmailQueuer(IServiceScopeFactory factory, EmailQueuerOptions options,
            ILogger<EmailQueuer<TContext>> logger)
        {
            this.factory = factory;
            this.options = options;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            hostStopping = stoppingToken;
            RequestPass();

            // Periodic wake (2.3.0). It is independent of the pass machinery on purpose: whatever
            // happens to a pass, the next wake still comes. It finds the rows that became due
            // without this process being told (another instance enqueued them, an admin screen
            // re-armed them, SQL inserted them).
            if (options.PollIntervalSeconds <= 0)
                return;

            var interval = TimeSpan.FromSeconds(Math.Max(MinPollIntervalSeconds, options.PollIntervalSeconds));
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(interval, stoppingToken);
                    RequestPass();
                }
            }
            catch (OperationCanceledException)
            {
                // host is stopping
            }
        }

        public override void Dispose()
        {
            lock (wakeLock)
            {
                disposed = true;
                wakeTimer?.Dispose();
                wakeTimer = null;
            }
            base.Dispose();
        }

        /// <summary>
        /// Asks for a drain of the queue. Returns at once: the drain runs in the background, and
        /// at most one runs at a time. A request made while a drain is running is remembered and
        /// served as soon as that drain ends.
        /// </summary>
        private void RequestPass()
        {
            if (hostStopping.IsCancellationRequested)
                return;

            Interlocked.Exchange(ref passRequested, 1);
            if (Interlocked.CompareExchange(ref passRunning, 1, 0) != 0)
                return; // a pass is running; it will see passRequested

            _ = Task.Run(() => RunPassesAsync());
        }

        /// <summary>
        /// Owns passRunning. The gate is always released (finally), so an exception thrown by a
        /// pass can no longer leave the worker believing a loop is running for the life of the
        /// process. A failed pass is not restarted in a loop: it arms one timed wake and exits.
        /// </summary>
        private async Task RunPassesAsync()
        {
            while (true)
            {
                try
                {
                    while (Interlocked.Exchange(ref passRequested, 0) == 1 && !hostStopping.IsCancellationRequested)
                    {
                        await DrainAsync();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Email queue pass failed; the next pass is scheduled in {Seconds} seconds", PassFailureRetrySeconds);
                    ArmWake(DateTime.UtcNow.AddSeconds(PassFailureRetrySeconds));
                }
                finally
                {
                    Volatile.Write(ref passRunning, 0);
                }

                // A request may have arrived between the last check above and the release.
                if (Volatile.Read(ref passRequested) == 0 || hostStopping.IsCancellationRequested)
                    return;
                if (Interlocked.CompareExchange(ref passRunning, 1, 0) != 0)
                    return; // another caller took the gate and will serve the request
            }
        }

        /// <summary>
        /// Arms the single wake timer for <paramref name="dueUtc"/>, replacing any earlier arming.
        /// The delay is floored at <see cref="MinWakeDelay"/>, so no timed wake can become a hot loop.
        /// </summary>
        private void ArmWake(DateTime dueUtc)
        {
            var delay = dueUtc - DateTime.UtcNow;
            if (delay < MinWakeDelay) delay = MinWakeDelay;
            if (delay > MaxWakeDelay) delay = MaxWakeDelay;

            lock (wakeLock)
            {
                if (disposed || hostStopping.IsCancellationRequested)
                    return;
                if (wakeTimer == null)
                    wakeTimer = new Timer(OnWakeTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                wakeTimer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        private void OnWakeTimer(object state)
        {
            try
            {
                RequestPass();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while starting a scheduled email queue pass");
            }
        }

        /// <summary>
        /// Add email to queue for sending
        /// </summary>
        /// <typeparam name="T">Type of the model should match the one in the template</typeparam>
        /// <param name="to">List of email addresses</param>
        /// <param name="template">Template email</param>
        /// <param name="model">The model with contains the data</param>
        /// <param name="subId">Optional unsubscribe link with subscription Id</param>
        /// <returns>Async task (does not wait for the email to be sent)</returns>
        public Task EnqueueAsync<T>(IEnumerable<string> to, string subject, string template, T model, string cc = "", string bcc = "", string attachementPaths = "")
        {
            return EnqueueAsync(string.Join(";", to), subject, template, model, cc, bcc, attachementPaths);
        }

        /// <summary>
        /// Add email to queue for sending
        /// </summary>
        /// <typeparam name="T">Type of the model should match the one in the template</typeparam>
        /// <param name="to">Email addresss or ';' seperated list of emails</param>
        /// <param name="template">Template email</param>
        /// <param name="model">The model with contains the data</param>
        /// <param name="subId">Optional unsubscribe link with subscription Id</param>
        /// <returns>Async task (does not wait for the email to be sent)</returns>
        public async Task EnqueueAsync<T>(string to, string subject, string template, T model, string cc = "", string bcc = "", string attachmentPaths = "")
        {
            await EnqueueCoreAsync(to, subject, template, model, cc, bcc, attachmentPaths, 0, null);
        }

        /// <summary>
        /// Add email to queue for sending, with a priority and/or a partition key (2.3.0)
        /// </summary>
        /// <typeparam name="T">Type of the model should match the one in the template</typeparam>
        /// <param name="to">List of email addresses</param>
        /// <param name="template">Template email</param>
        /// <param name="model">The model with contains the data</param>
        /// <param name="enqueueOptions">CC, BCC, attachments, priority and partition key; null means the defaults</param>
        /// <returns>Async task (does not wait for the email to be sent)</returns>
        public Task EnqueueAsync<T>(IEnumerable<string> to, string subject, string template, T model, EmailEnqueueOptions enqueueOptions)
        {
            return EnqueueAsync(string.Join(";", to), subject, template, model, enqueueOptions);
        }

        /// <summary>
        /// Add email to queue for sending, with a priority and/or a partition key (2.3.0)
        /// </summary>
        /// <typeparam name="T">Type of the model should match the one in the template</typeparam>
        /// <param name="to">Email addresss or ';' seperated list of emails</param>
        /// <param name="template">Template email</param>
        /// <param name="model">The model with contains the data</param>
        /// <param name="enqueueOptions">CC, BCC, attachments, priority and partition key; null means the defaults</param>
        /// <exception cref="ArgumentException">If the partition key is longer than 64 characters</exception>
        /// <returns>Async task (does not wait for the email to be sent)</returns>
        public async Task EnqueueAsync<T>(string to, string subject, string template, T model, EmailEnqueueOptions enqueueOptions)
        {
            var o = enqueueOptions ?? new EmailEnqueueOptions();
            await EnqueueCoreAsync(to, subject, template, model, o.Cc ?? "", o.Bcc ?? "", o.AttachmentPaths ?? "",
                o.Priority, o.PartitionKey);
        }

        private async Task EnqueueCoreAsync<T>(string to, string subject, string template, T model, string cc, string bcc,
            string attachmentPaths, int priority, string partitionKey)
        {
            if (partitionKey != null && partitionKey.Length > EmailQueuerTask.PartitionKeyMaxLength)
                throw new ArgumentException(
                    $"Partition key cannot be longer than {EmailQueuerTask.PartitionKeyMaxLength} characters", nameof(partitionKey));

            using (var scope = factory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<TContext>();
                context.EmailQueuerTasks.Add(new EmailQueuerTask
                {
                    To = to,
                    CC = cc,
                    BCC = bcc,
                    AttachmentPaths = attachmentPaths,
                    Subject = subject,
                    Template = template,
                    CreatedOn = DateTime.UtcNow,
                    ModelType = typeof(T).FullName,
                    ModelJson = JsonConvert.SerializeObject(model),
                    Status = EmailTaskStatus.Pending,
                    Priority = priority,
                    PartitionKey = partitionKey
                });
                await context.SaveChangesAsync();
            }

            if (options.RunHostedService)
            {
                RequestPass();
            }
        }

        /// <summary>
        /// Sends an email by using an email task object, do not call this method manually as it will take time
        /// and kill the performance unless it's you want to wait for the email sending to complete
        /// </summary>
        /// <param name="emailTask">Email task object</param>
        /// <exception cref="System.Net.Mail.SmtpException">If the email sending failed</exception>
        /// <returns>Async task whenever sending the email is done</returns>
        public async Task ManuallySendEmailAsync(EmailQueuerTask emailTask)
        {
            using var scope = factory.CreateScope();
            var fluent = scope.ServiceProvider.GetRequiredService<IFluentEmailFactory>();
            // Its own connection: a manual send may run while a pass is using the pass's one.
            using var delivery = CreateDelivery();
            var email = await BuildEmailAsync(emailTask, fluent);
            await DeliverAsync(email, delivery);
        }

        /// <summary>
        /// The send timeout in force: <see cref="EmailQueuerOptions.SendTimeoutSeconds"/> (a value of
        /// 0 or less means the default), at least <see cref="MinSendTimeoutSeconds"/>, and never more
        /// than 80% of the stale-claim window so a send always ends before another instance could
        /// reclaim its row.
        /// </summary>
        internal TimeSpan EffectiveSendTimeout()
        {
            var staleCeiling = (long)Math.Max(1, options.StaleClaimMinutes) * 60 * 4 / 5;
            long configured = options.SendTimeoutSeconds > 0 ? options.SendTimeoutSeconds : EmailQueuerOptions.DefaultSendTimeoutSeconds;
            var seconds = Math.Max(MinSendTimeoutSeconds, Math.Min(configured, staleCeiling));
            return TimeSpan.FromSeconds(seconds);
        }

        private SmtpDelivery CreateDelivery()
        {
            return new SmtpDelivery(options.SmtpClient ?? new System.Net.Mail.SmtpClient(), EffectiveSendTimeout(),
                MaxSmtpConnectionIdle, MaxMessagesPerSmtpConnection);
        }

        /// <summary>
        /// Everything that depends on the message alone: model, template, addresses, attachments.
        /// A failure here is the message's fault and never counts towards the outage pause.
        /// </summary>
        private async Task<IFluentEmail> BuildEmailAsync(EmailQueuerTask emailTask, IFluentEmailFactory fluent)
        {
            var data = JsonConvert.DeserializeObject(emailTask.ModelJson,
                options.Assembly.Assembly.GetType(emailTask.ModelType));

            var emails = emailTask.To.Split(";")
                .Select(e => new FluentEmail.Core.Models.Address { EmailAddress = e })
                .ToArray();

            FluentEmail.Core.Models.Address[] cc = { };
            if (!string.IsNullOrEmpty(emailTask.CC))
               cc = emailTask.CC.Split(";")
                    .Select(e => new FluentEmail.Core.Models.Address { EmailAddress = e })
                    .ToArray();

            FluentEmail.Core.Models.Address[] bcc = { };
            if (!string.IsNullOrEmpty(emailTask.BCC))
               bcc = emailTask.BCC.Split(";")
                .Select(e => new FluentEmail.Core.Models.Address { EmailAddress = e })
                .ToArray();

            var attachmentFileNames = (emailTask.AttachmentPaths ?? "").Split(";");

            var body = await GenerateEmailBody(emailTask.Template, data);

            var emailToSend = fluent.Create()
                .To(emails)
                .CC(cc)
                .BCC(bcc)
                .Subject(emailTask.Subject)
                .Body(body, true);

            foreach(var attachmentFileName in attachmentFileNames)
            {
                if (!string.IsNullOrEmpty(attachmentFileName))
                    emailToSend.AttachFromFilename(attachmentFileName);
            }

            return emailToSend;
        }

        /// <summary>
        /// The send call itself, bounded by <see cref="EffectiveSendTimeout"/> (60 seconds by
        /// default), so a send that hangs fails like any other send: the row records the error and
        /// waits its backoff, and the queue moves on to the next email.
        ///
        /// With the SMTP sender AddEmailQueuer registers, the message goes through
        /// <paramref name="delivery"/>, which owns the connection and aborts it on timeout
        /// (2.3.0-rc.2: FluentEmail's SmtpSender ignores cancellation and keeps one connection open
        /// for the life of the process, see <see cref="SmtpDelivery"/>). Any other ISender the host
        /// registered is still called and the wait for it is bounded the same way; a send it does
        /// not stop when cancelled is abandoned, not awaited. A response the sender marks
        /// unsuccessful is a failure.
        /// </summary>
        private async Task DeliverAsync(IFluentEmail email, SmtpDelivery delivery)
        {
            if (email.Sender is FluentEmail.Smtp.SmtpSender && options.SmtpClient != null)
            {
                await delivery.SendAsync(email.Data);
                return;
            }

            var sendTimeout = delivery.SendTimeout;
            using var timeout = new CancellationTokenSource(sendTimeout);
            var send = email.SendAsync(timeout.Token);
            var finished = await Task.WhenAny(send, Task.Delay(sendTimeout));
            if (finished != send)
            {
                _ = send.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException($"The send did not complete within {(long)sendTimeout.TotalSeconds} seconds and was abandoned");
            }

            FluentEmail.Core.Models.SendResponse response;
            try
            {
                response = await send;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"The send did not complete within {(long)sendTimeout.TotalSeconds} seconds and was cancelled");
            }

            if (response != null && !response.Successful)
            {
                var errors = response.ErrorMessages == null ? "" : string.Join("; ", response.ErrorMessages);
                throw new InvalidOperationException($"The email sender reported a failure: {errors}");
            }
        }

        private async Task<string> GenerateEmailBody(string template, object model)
        {
            var viewDataOrViewBag = new Dictionary<string, object>();
            viewDataOrViewBag["Subject"] = template.ToString();

            logger.LogInformation($"GenerateEmailBody with template path : {string.Format(options.TemplatePath, template)}");

            var html = await RazorTemplateEngine.RenderAsync(string.Format(options.TemplatePath, template), model, viewDataOrViewBag);
            if (options.MoveCssInline)
            {
                using var preMailer = new PreMailer.Net.PreMailer(html);
                html = preMailer.MoveCssInline().Html;
            }
            return html;
        }

        /// <summary>
        /// One pass: sends everything that is due, then arms the wake for the next retry and returns.
        ///
        /// SQL per round (every command is a telemetry row on a monitored host, so the count is part
        /// of the design): one read of the queue head grouped by priority and partition, one read of
        /// up to <see cref="ClaimBatchSize"/> full rows, then per email one claim and one save. An
        /// idle pass is two reads. The stale-claim reclaim costs nothing unless a stale row exists,
        /// because the head read is what detects it. A probe round after an outage pause costs the
        /// same two reads: the probe candidates are read in place of the batch.
        ///
        /// Every turn of the loop either claims at least one row or returns, and a claimed row
        /// leaves the due set (Sent, PermanentlyFailed, or Error with a future NextAttemptOn), so
        /// the loop cannot spin.
        /// </summary>
        private async Task DrainAsync()
        {
            // Outage pause: no SQL and no send until it ends. The timer is re-armed rather than
            // assumed, so a wake that lands a moment early cannot strand the queue.
            var pausedUntil = new DateTime(Interlocked.Read(ref pausedUntilTicks), DateTimeKind.Utc);
            if (DateTime.UtcNow < pausedUntil)
            {
                ArmWake(pausedUntil);
                return;
            }

            ForgetStaleOutage(DateTime.UtcNow);

            using var scope = factory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var fluent = scope.ServiceProvider.GetRequiredService<IFluentEmailFactory>();
            // At most one SMTP connection at a time for the pass, reused only while fresh and closed
            // (QUIT) when the pass ends. The next pass opens a new one.
            using var delivery = CreateDelivery();

            var staleMinutes = Math.Max(1, options.StaleClaimMinutes);
            var fairness = options.PartitionFairness;
            var reclaimedThisPass = false;

            while (!hostStopping.IsCancellationRequested)
            {
                // Requests made up to here are covered by the read below; later ones re-run the pass.
                Interlocked.Exchange(ref passRequested, 0);

                var now = DateTime.UtcNow;
                var legacyCutoff = now.AddMinutes(-staleMinutes);

                // Queue head: the oldest due row of every (priority, partition), plus, in the same
                // read, any stale claim. A stale claim is a row left in Sending past its stamped
                // deadline (the worker died mid-send), or a Sending row with no deadline (claimed
                // by pre-2.2.0 code) older than the window by CreatedOn.
                var groups = await context.EmailQueuerTasks
                    .AsNoTracking()
                    .Where(x => ((x.Status == EmailTaskStatus.Pending || x.Status == EmailTaskStatus.Error)
                                 && (x.NextAttemptOn == null || x.NextAttemptOn <= now))
                                || (x.Status == EmailTaskStatus.Sending
                                    && ((x.NextAttemptOn != null && x.NextAttemptOn <= now)
                                        || (x.NextAttemptOn == null && x.CreatedOn <= legacyCutoff))))
                    .GroupBy(x => new { x.Priority, x.PartitionKey, x.Status })
                    .Select(g => new { g.Key.Priority, g.Key.PartitionKey, g.Key.Status, HeadId = g.Min(x => x.Id) })
                    .ToListAsync();

                if (!reclaimedThisPass && groups.Any(g => g.Status == EmailTaskStatus.Sending))
                {
                    // At most once per pass: if the reclaim leaves stale rows behind, the next
                    // pass deals with them; this one must not loop on them.
                    reclaimedThisPass = true;
                    await ReclaimStaleClaimsAsync(context, now, legacyCutoff, staleMinutes);
                    continue;
                }

                var due = groups.Where(g => g.Status != EmailTaskStatus.Sending).ToList();
                if (due.Count == 0)
                {
                    await ArmForNextDueAsync(context);
                    return; // Exit to release resources; a wake will restart the pass when needed.
                }

                // Highest priority first. Lower priorities are not touched while it has due mail.
                var topPriority = due.Max(g => g.Priority);

                // Within it, one lane per partition key (a single lane when fairness is off).
                var lanes = new Dictionary<string, Lane>();
                foreach (var g in due)
                {
                    if (g.Priority != topPriority) continue;
                    var laneKey = LaneKeyOf(g.PartitionKey, fairness);
                    if (!lanes.TryGetValue(laneKey, out var lane))
                        lanes[laneKey] = new Lane { LaneKey = laneKey, PartitionKey = g.PartitionKey, HeadId = g.HeadId };
                    else if (g.HeadId < lane.HeadId)
                        lane.HeadId = g.HeadId;
                }

                // An outage has been declared and its pause is over: this round sends ONE email
                // as a probe instead of a batch.
                var probing = IsOutageDeclared();

                List<EmailQueuerTask> batch;
                if (probing)
                {
                    // The probe is the due email most likely to get through if the server is up,
                    // whatever its priority or partition: the fewest failed attempts first, then
                    // the higher priority, then the newest. It must NOT be the queue head by Id:
                    // a message the server refuses for its own content sits at the head, is due
                    // again at every pause end, and as the permanent probe would hold every email
                    // queued behind it until its own retry budget ran out. Newest first for the
                    // same reason: mail queued after a run of such messages is the mail to try.
                    var candidates = await context.EmailQueuerTasks
                        .AsNoTracking()
                        .Where(x => (x.Status == EmailTaskStatus.Pending || x.Status == EmailTaskStatus.Error)
                                    && (x.NextAttemptOn == null || x.NextAttemptOn <= now))
                        .OrderBy(x => x.RetryCount)
                        .ThenByDescending(x => x.Priority)
                        .ThenByDescending(x => x.Id)
                        .Take(ClaimBatchSize)
                        .ToListAsync();

                    // Emails that have not failed in this outage go first (the sort is stable, so
                    // the order above holds within each half). One that already failed is the
                    // probe only when nothing else is among the candidates.
                    batch = candidates.OrderBy(r => outageRowIds.Contains(r.Id) ? 1 : 0).ToList();
                }
                else if (lanes.Count == 1)
                {
                    // Nobody to take turns with: load the next rows of this lane in one read. The
                    // Id floor lets the read start at the head instead of scanning the table.
                    var only = lanes.Values.First();
                    var headId = only.HeadId;
                    var query = context.EmailQueuerTasks
                        .AsNoTracking()
                        .Where(x => x.Id >= headId
                                    && x.Priority == topPriority
                                    && (x.Status == EmailTaskStatus.Pending || x.Status == EmailTaskStatus.Error)
                                    && (x.NextAttemptOn == null || x.NextAttemptOn <= now));
                    if (fairness)
                    {
                        var partitionKey = only.PartitionKey;
                        query = partitionKey == null
                            ? query.Where(x => x.PartitionKey == null)
                            : query.Where(x => x.PartitionKey == partitionKey);
                    }
                    batch = await query.OrderBy(x => x.Id).Take(ClaimBatchSize).ToListAsync();
                }
                else
                {
                    // Taking turns: one row per partition, the partition served least recently
                    // first (never served before anything; ties go to the older row).
                    var headIds = lanes.Values
                        .OrderBy(l => LastServedOf(l.LaneKey))
                        .ThenBy(l => l.HeadId)
                        .Take(ClaimBatchSize)
                        .Select(l => l.HeadId)
                        .ToList();
                    // The due predicate is repeated here on purpose. Between the head read and
                    // this one another worker instance may have claimed, sent or failed a head
                    // row; without the predicate that row would come back as Sending, Sent or
                    // not-yet-due and the claim below, which compares against the status as
                    // read, would take it and send it a second time.
                    var rows = await context.EmailQueuerTasks
                        .AsNoTracking()
                        .Where(x => headIds.Contains(x.Id)
                                    && (x.Status == EmailTaskStatus.Pending || x.Status == EmailTaskStatus.Error)
                                    && (x.NextAttemptOn == null || x.NextAttemptOn <= now))
                        .ToListAsync();
                    batch = rows.OrderBy(r => headIds.IndexOf(r.Id)).ToList();
                }

                var claimedAny = false;
                foreach (var row in batch)
                {
                    if (hostStopping.IsCancellationRequested)
                        return;

                    var laneKey = LaneKeyOf(row.PartitionKey, fairness);
                    var outcome = await ProcessRowAsync(context, fluent, delivery, row, staleMinutes);
                    if (outcome == RowOutcome.NotClaimed)
                        continue; // another instance won the row

                    claimedAny = true;
                    MarkServed(laneKey);

                    if (outcome == RowOutcome.Paused)
                        return; // the wake for the end of the pause is already armed

                    // The probe got through: the outage is over and the next round goes back to
                    // priority and turns. (A probe that failed for the message's own reasons says
                    // nothing about the server, so the next candidate is tried.)
                    if (probing && outcome == RowOutcome.Sent)
                        break;
                }

                if (!claimedAny)
                {
                    // Every row of the round was taken by another instance (or vanished). That
                    // instance is draining; look again later instead of racing it in a tight loop.
                    ArmWake(DateTime.UtcNow + LostRoundDelay);
                    return;
                }
            }
        }

        /// <summary>
        /// Returns stale claims to the queue. Each one counts as a failed attempt: RetryCount is
        /// incremented and the reason recorded, and a row already at the end of its budget is
        /// retired instead of being re-queued, so a row that kills its worker every time cannot
        /// cycle for ever. Two plain UPDATEs, run only when the head read saw a stale claim.
        /// </summary>
        private async Task ReclaimStaleClaimsAsync(TContext context, DateTime now, DateTime legacyCutoff, int staleMinutes)
        {
            var maxRetryAttempts = options.MaxRetryAttempts;
            var error = $"Claim expired after {staleMinutes} minutes: the worker stopped or the send did not return";
            DateTime? errorOn = now;

            var retired = await context.EmailQueuerTasks
                .Where(x => x.Status == EmailTaskStatus.Sending
                            && ((x.NextAttemptOn != null && x.NextAttemptOn <= now)
                                || (x.NextAttemptOn == null && x.CreatedOn <= legacyCutoff))
                            && x.RetryCount >= maxRetryAttempts)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, EmailTaskStatus.PermanentlyFailed)
                    .SetProperty(x => x.RetryCount, x => x.RetryCount + 1)
                    .SetProperty(x => x.LastError, error)
                    .SetProperty(x => x.LastErrorOn, errorOn));

            var requeued = await context.EmailQueuerTasks
                .Where(x => x.Status == EmailTaskStatus.Sending
                            && ((x.NextAttemptOn != null && x.NextAttemptOn <= now)
                                || (x.NextAttemptOn == null && x.CreatedOn <= legacyCutoff))
                            && x.RetryCount < maxRetryAttempts)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, EmailTaskStatus.Error)
                    .SetProperty(x => x.RetryCount, x => x.RetryCount + 1)
                    .SetProperty(x => x.LastError, error)
                    .SetProperty(x => x.LastErrorOn, errorOn));

            if (retired > 0 || requeued > 0)
                logger.LogWarning("Reclaimed stale email claims: {Requeued} re-queued, {Retired} permanently failed", requeued, retired);
        }

        /// <summary>
        /// Nothing is due. If retries are scheduled, arm the wake for the earliest one.
        /// </summary>
        private async Task ArmForNextDueAsync(TContext context)
        {
            var nextRetryTime = await context.EmailQueuerTasks
                .AsNoTracking()
                .Where(x => (x.Status == EmailTaskStatus.Pending || x.Status == EmailTaskStatus.Error)
                            && x.NextAttemptOn != null)
                .OrderBy(x => x.NextAttemptOn)
                .Select(x => x.NextAttemptOn)
                .FirstOrDefaultAsync();

            if (nextRetryTime != null)
            {
                logger.LogDebug("Scheduling the next email queue pass for {NextAttemptOn:o}", nextRetryTime.Value);
                ArmWake(nextRetryTime.Value);
            }
        }

        /// <summary>
        /// Claims one row, sends it and records the result.
        /// </summary>
        private async Task<RowOutcome> ProcessRowAsync(TContext context, IFluentEmailFactory fluent, SmtpDelivery delivery,
            EmailQueuerTask emailTask, int staleMinutes)
        {
            // Atomic claim: CAS Pending/Error -> Sending only if the row is still as it was read
            // (same status and same RetryCount, so a row another instance sent or failed in the
            // meantime is not taken). NextAttemptOn is stamped with the claim deadline so the
            // reclaim can recover the row if this worker dies mid-send. 0 rows => someone else won.
            var taskId = emailTask.Id;
            var seenStatus = emailTask.Status;
            var seenRetryCount = emailTask.RetryCount;

            // The claim compares against the status as read, so it is only a guarantee when that
            // status is one a claim may start from. A row read as Sending, Sent or
            // PermanentlyFailed is never claimed, whatever read produced it.
            if (seenStatus != EmailTaskStatus.Pending && seenStatus != EmailTaskStatus.Error)
                return RowOutcome.NotClaimed;

            var claimDeadline = DateTime.UtcNow.AddMinutes(staleMinutes);
            var claimed = await context.EmailQueuerTasks
                .Where(x => x.Id == taskId && x.Status == seenStatus && x.RetryCount == seenRetryCount)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, EmailTaskStatus.Sending)
                    .SetProperty(x => x.NextAttemptOn, claimDeadline));
            if (claimed == 0)
                return RowOutcome.NotClaimed;

            // Track the row from the just-claimed state so the save below issues a correct UPDATE
            // of only what changes. The tracker is cleared after every row: the context never
            // accumulates the rows of a long drain.
            emailTask.Status = EmailTaskStatus.Sending;
            emailTask.NextAttemptOn = claimDeadline;
            context.EmailQueuerTasks.Attach(emailTask);

            logger.LogInformation("Sending email task {TaskId} (Retry {RetryCount})", taskId, emailTask.RetryCount);

            Exception failure = null;
            var sendStarted = false;
            try
            {
                var email = await BuildEmailAsync(emailTask, fluent);
                sendStarted = true;
                await DeliverAsync(email, delivery);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure == null)
            {
                logger.LogInformation("Email task {TaskId} sent successfully", taskId);

                emailTask.Status = EmailTaskStatus.Sent;
                emailTask.SentOn = DateTime.UtcNow;
                emailTask.LastError = null;
                emailTask.LastErrorOn = null;
                await SaveAndReleaseAsync(context);

                // The mail server answered: any outage is over.
                ResetOutageState();
                return RowOutcome.Sent;
            }

            logger.LogError(failure, "Error while sending email task {TaskId}", taskId);

            var failedOn = DateTime.UtcNow;
            emailTask.RetryCount++;
            emailTask.LastError = DescribeError(failure);
            emailTask.LastErrorOn = failedOn;
            if (emailTask.RetryCount > options.MaxRetryAttempts)
            {
                emailTask.Status = EmailTaskStatus.PermanentlyFailed;
                logger.LogWarning("Email task {TaskId} permanently failed after {Sends} sends", taskId, emailTask.RetryCount);
            }
            else
            {
                emailTask.Status = EmailTaskStatus.Error;
                var delay = options.GetRetryDelay(emailTask.RetryCount);
                emailTask.NextAttemptOn = failedOn.Add(delay);
                logger.LogInformation("Scheduling retry #{RetryCount} for task {TaskId} in {Seconds} seconds",
                    emailTask.RetryCount, taskId, (long)delay.TotalSeconds);
            }
            await SaveAndReleaseAsync(context);

            // Outage pause. Only a failure of the send call itself can be the mail server's fault;
            // a model, template, address or attachment problem says nothing about the server and
            // leaves the count as it is. Nothing is tracked when the pause is turned off.
            if (options.OutagePauseThreshold > 0 && sendStarted && IsTransportFailure(failure))
            {
                lastTransportFailureTicks = DateTime.UtcNow.Ticks;

                // Each email counts once per outage. A retry of an email already counted adds
                // nothing: a server that refuses one message for its own content (size, content
                // policy) raises the same exception type as a server that is down, and that one
                // message must not be able to pause the queue by being retried.
                if (!outageRowIds.Contains(taskId))
                {
                    if (outageRowIds.Count < MaxTrackedOutageRows)
                        outageRowIds.Add(taskId);
                    if (outageFailedRows < int.MaxValue)
                        outageFailedRows++;
                }

                // Below the threshold this is one failed email among others. At or above it an
                // outage is declared: the first time this ends the batch, afterwards it is a
                // failed probe, and either way the worker pauses.
                if (IsOutageDeclared())
                {
                    if (outagePauseStep < MaxOutagePauseStep)
                        outagePauseStep++;
                    var pause = options.GetOutagePauseDelay(outagePauseStep);
                    var until = DateTime.UtcNow.Add(pause);
                    Interlocked.Exchange(ref pausedUntilTicks, until.Ticks);
                    ArmWake(until);
                    logger.LogWarning(
                        "Email queue paused for {Seconds} seconds: {Emails} different emails have failed on transport with no successful send in between. One email will then be tried as a probe. Emails not attempted keep their retry budget",
                        (long)pause.TotalSeconds, outageFailedRows);
                    return RowOutcome.Paused;
                }
            }

            return RowOutcome.Failed;
        }

        /// <summary>
        /// True once at least <see cref="EmailQueuerOptions.OutagePauseThreshold"/> different emails
        /// have failed on transport with no successful send in between. From then until a send
        /// succeeds (or the state goes stale) the worker sends one probe per pause, not batches.
        /// </summary>
        private bool IsOutageDeclared()
        {
            var threshold = options.OutagePauseThreshold;
            return threshold > 0 && outageFailedRows >= threshold;
        }

        private void ResetOutageState()
        {
            outageFailedRows = 0;
            outagePauseStep = 0;
            outageRowIds.Clear();
            lastTransportFailureTicks = 0;
        }

        /// <summary>
        /// Drops outage state that nothing has confirmed for a while. Without it the count and the
        /// pause step survive for the life of the process when the emails that raised them end
        /// PermanentlyFailed with no successful send after them, and one transient failure days
        /// later would pause the queue for the longest pause straight away.
        ///
        /// The age is measured from the later of the last transport failure and the end of the
        /// last pause, and the window is the longest pause (floor 5 minutes). During a real outage
        /// the probe runs when its pause ends and fails again, so the state is always fresh there:
        /// this never cuts a live outage short.
        /// </summary>
        private void ForgetStaleOutage(DateTime nowUtc)
        {
            if (outageFailedRows == 0 && outagePauseStep == 0)
                return;

            var lastSign = Math.Max(lastTransportFailureTicks, Interlocked.Read(ref pausedUntilTicks));
            var memory = options.GetOutagePauseDelay(MaxOutagePauseStep);
            if (memory < MinOutageMemory) memory = MinOutageMemory;

            if (nowUtc.Ticks - lastSign > memory.Ticks)
            {
                logger.LogInformation("Email queue outage state cleared: no transport failure for {Seconds} seconds", (long)memory.TotalSeconds);
                ResetOutageState();
            }
        }

        private static async Task SaveAndReleaseAsync(TContext context)
        {
            try
            {
                await context.SaveChangesAsync();
            }
            finally
            {
                context.ChangeTracker.Clear();
            }
        }

        private static string LaneKeyOf(string partitionKey, bool fairness)
        {
            return fairness && partitionKey != null ? partitionKey : NullPartitionLane;
        }

        private long LastServedOf(string laneKey)
        {
            return lastServed.TryGetValue(laneKey, out var sequence) ? sequence : 0;
        }

        private void MarkServed(string laneKey)
        {
            lastServed[laneKey] = ++serveSequence;
            if (lastServed.Count <= MaxTrackedPartitions)
                return;

            // Prune: forget the half served longest ago. A forgotten lane counts as never served,
            // which is the right side to err on for the lanes that have waited longest.
            var forget = lastServed.OrderBy(p => p.Value).Take(lastServed.Count / 2).Select(p => p.Key).ToList();
            foreach (var key in forget)
                lastServed.Remove(key);
        }

        /// <summary>
        /// True when the failure of the send call points at the mail server or the connection to
        /// it rather than at this message: an SMTP error that is not a recipient rejection (this
        /// includes "Failure sending mail" and a protocol violation), an I/O or socket error
        /// anywhere in the chain (a connection closed mid-conversation), a TLS failure, a timeout.
        ///
        /// This cannot tell every message-specific failure apart: a server that refuses one message
        /// at the end of DATA (size limit, content policy) raises a plain SmtpException too, and a
        /// send that times out on one large attachment is a timeout. That is why the caller counts
        /// each email once per outage and never probes with the queue head.
        /// </summary>
        private static bool IsTransportFailure(Exception failure)
        {
            var transport = false;
            var depth = 0;
            for (var e = failure; e != null && depth < 8; e = e.InnerException, depth++)
            {
                // The server answered and refused these recipients: the message's fault.
                if (e is System.Net.Mail.SmtpFailedRecipientException)
                    return false;

                if (e is System.Net.Mail.SmtpException
                    || e is System.IO.IOException
                    || e is System.Net.Sockets.SocketException
                    || e is System.Security.Authentication.AuthenticationException
                    || e is TimeoutException)
                    transport = true;
            }
            return transport;
        }

        /// <summary>
        /// "{type}: {message}" for the exception and up to three inner exceptions, with the SMTP
        /// status code when there is one. No stack trace. One line, at most 1000 characters.
        /// </summary>
        private static string DescribeError(Exception failure)
        {
            var text = new StringBuilder();
            var depth = 0;
            for (var e = failure; e != null && depth <= 3; e = e.InnerException, depth++)
            {
                if (depth > 0) text.Append(" <- ");
                text.Append(e.GetType().Name);
                if (e is System.Net.Mail.SmtpException smtp)
                    text.Append(" (").Append((int)smtp.StatusCode).Append(' ').Append(smtp.StatusCode).Append(')');
                text.Append(": ").Append(e.Message);
            }

            text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
            if (text.Length > EmailQueuerTask.LastErrorMaxLength)
                text.Length = EmailQueuerTask.LastErrorMaxLength;
            return text.ToString();
        }
    }
}
