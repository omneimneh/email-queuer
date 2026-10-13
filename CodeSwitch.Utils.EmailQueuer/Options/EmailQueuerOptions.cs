using Microsoft.Extensions.Configuration;
using System;
using System.Dynamic;
using System.Net;
using System.Net.Mail;

namespace CodeSwitch.Utils.EmailQueuer.Options
{
    public class EmailQueuerOptions
    {
        /// <summary>
        /// Ceiling on one retry wait when <see cref="MaxRetryBackoffSeconds"/> is 0 (24 hours).
        /// </summary>
        private const long UncappedBackoffCeilingSeconds = 86400;
        /// <summary>
        /// Ceiling on one outage pause when <see cref="MaxRetryBackoffSeconds"/> is 0 (15 minutes).
        /// </summary>
        private const long UncappedOutagePauseCeilingSeconds = 900;

        /// <summary>
        /// The network credentials of the sender
        /// </summary>
        public NetworkCredential Sender { get => _sender; set => SmtpClient.Credentials = _sender = value; }
        /// <summary>
        /// Smtp client options
        /// </summary>
        public SmtpClient SmtpClient { get; set; }
        /// <summary>
        /// Css is only supported inline in emails, however setting this to true will automtically
        /// allow your global css to take effect so you don't have to write repetitive inline chunks
        /// </summary>
        public bool MoveCssInline { get; set; }
        /// <summary>
        /// Default view bag object for razor rendering
        /// </summary>
        public dynamic ViewBag { get; set; }
        /// <summary>
        /// Dot seperated list of folders starting from the assembly class path until the razor file
        /// must contain {0} (e.g. the default is "{0}.cshtml")
        /// </summary>
        public string TemplatePath { get; set; }
        /// <summary>
        /// A class at the root of the template folder, razor models should exist in the same assembly
        /// </summary>
        public Type Assembly { get; set; }
    /// <summary>
    /// Number of waits before a failing email is marked permanently failed. N means N waits and
    /// therefore N + 1 sends: the row ends at RetryCount = N + 1. Default 3.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;
    /// <summary>
    /// First wait in seconds before the next retry attempt; each further wait doubles, up to
    /// <see cref="MaxRetryBackoffSeconds"/>. Default 30.
    /// </summary>
    public int RetryBackoffSeconds { get; set; } = 30;
    /// <summary>
    /// When false the background worker (hosted service) is NOT registered by AddEmailQueuer;
    /// the EmailQueuer&lt;TContext&gt; singleton stays available so producers can still EnqueueAsync.
    /// Use on parked/standby instances that must not drain the shared queue. Default true.
    /// </summary>
    public bool RunHostedService { get; set; } = true;
    /// <summary>
    /// Minutes a row may sit in Sending before it is considered orphaned and reclaimed to Error.
    /// Must exceed the max realistic single-send time: since 2.3.0 a send still running after 80%
    /// of this window is cancelled and recorded as a failed attempt. Default 10.
    /// </summary>
    public int StaleClaimMinutes { get; set; } = 10;
        /// <summary>
        /// Ceiling in seconds on one retry wait (2.3.0). 0 (the default) means no configured ceiling,
        /// as before 2.3.0; a fixed ceiling of 24 hours then applies so the wait can never overflow.
        /// Total budget for one email that keeps failing = the sum of the <see cref="MaxRetryAttempts"/>
        /// waits (see <see cref="GetRetryDelay"/>).
        /// </summary>
        public int MaxRetryBackoffSeconds { get; set; } = 0;
        /// <summary>
        /// Number of different emails whose send must fail on transport (the mail server
        /// unreachable, closing the connection, refusing the session or timing out), with no
        /// successful send in between, before the worker stops draining and waits, then tries a
        /// single email as a probe (2.3.0). An email counts once however often it is retried, so
        /// one message that keeps failing never pauses the queue. The emails the worker did not
        /// attempt keep their whole retry budget. 0 (the default) turns the pause off.
        /// </summary>
        public int OutagePauseThreshold { get; set; } = 0;
        /// <summary>
        /// Within one priority, take turns between the distinct partition keys (2.3.0). When false
        /// the oldest due email of the highest priority goes first whatever its key. Default true;
        /// it changes nothing for a queue whose rows carry no partition key.
        /// </summary>
        public bool PartitionFairness { get; set; } = true;
        /// <summary>
        /// Seconds between periodic wakes of the worker (2.3.0). The wake picks up rows that became
        /// due without this process knowing: enqueued by another instance, re-armed by an admin
        /// screen, inserted by SQL. An idle wake costs two small queries. Values from 1 to 29 are
        /// raised to 30. 0 turns the periodic wake off (the pre-2.3.0 behaviour). Default 120.
        /// </summary>
        public int PollIntervalSeconds { get; set; } = 120;

        private NetworkCredential _sender;

        public static EmailQueuerOptions Default => new EmailQueuerOptions
        {
            SmtpClient = new SmtpClient
            {
                UseDefaultCredentials = false,
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network
            },
            TemplatePath = "{0}.cshtml",
            ViewBag = new ExpandoObject(),
            MaxRetryAttempts = 3,
            RetryBackoffSeconds = 30
        };

        /// <summary>
        /// The wait before the next attempt of an email that has failed <paramref name="retryCount"/>
        /// times: <c>min(ceiling, RetryBackoffSeconds * 2^(retryCount - 1))</c>, computed without
        /// ever exceeding the ceiling, so it cannot overflow whatever the retry count.
        /// </summary>
        public TimeSpan GetRetryDelay(int retryCount)
        {
            var ceiling = MaxRetryBackoffSeconds > 0 ? MaxRetryBackoffSeconds : UncappedBackoffCeilingSeconds;
            return TimeSpan.FromSeconds(ComputeBackoffSeconds(RetryBackoffSeconds, ceiling, retryCount));
        }

        /// <summary>
        /// The length of the <paramref name="pauseStep"/>-th consecutive outage pause: the same
        /// doubling as the retry wait, capped at <see cref="MaxRetryBackoffSeconds"/> (15 minutes
        /// when that is 0).
        /// </summary>
        public TimeSpan GetOutagePauseDelay(int pauseStep)
        {
            var ceiling = MaxRetryBackoffSeconds > 0 ? MaxRetryBackoffSeconds : UncappedOutagePauseCeilingSeconds;
            return TimeSpan.FromSeconds(ComputeBackoffSeconds(RetryBackoffSeconds, ceiling, pauseStep));
        }

        /// <summary>
        /// Doubles <paramref name="baseSeconds"/> (step - 1) times but stops as soon as the ceiling
        /// is reached. The running value is doubled only while it is below the ceiling, and the
        /// ceiling is at most int.MaxValue, so the product always fits a long: there is no step
        /// count for which this overflows or goes negative, and the loop ends within 32 rounds.
        /// </summary>
        internal static long ComputeBackoffSeconds(long baseSeconds, long ceilingSeconds, int step)
        {
            if (ceilingSeconds < 1) ceilingSeconds = 1;
            if (ceilingSeconds > int.MaxValue) ceilingSeconds = int.MaxValue;
            if (baseSeconds < 1) baseSeconds = 1;

            var wait = Math.Min(baseSeconds, ceilingSeconds);
            for (var i = 1; i < step && wait < ceilingSeconds; i++)
            {
                wait = Math.Min(wait * 2, ceilingSeconds);
            }
            return wait;
        }

        public void LoadFromConfiguration(IConfiguration configuration, string sectionName = nameof(EmailQueuer))
        {
            if (configuration == null)
                throw new ArgumentException("Configuration object cannot be null");

            if (sectionName == null)
                throw new ArgumentException("Configuration section name cannot be null");

            SmtpClient = Default.SmtpClient;
            var options = configuration.GetSection(sectionName).Get<EmailSenderConfiguration>();

            SmtpClient.Host = options.Smtp.Host;
            SmtpClient.Port = options.Smtp.Port;
            if (options.Smtp.EnableSsl != null)
                SmtpClient.EnableSsl = options.Smtp.EnableSsl.Value;
            if (options.Smtp.Timeout != null)
                SmtpClient.Timeout = options.Smtp.Timeout.Value;
            Sender = new NetworkCredential(options.Sender.Email, options.Sender.Password);
            SmtpClient.Credentials = Sender;

            ViewBag = options.ViewBag ?? Default.ViewBag;
            MoveCssInline = options.MoveCssInline;
            TemplatePath = options.TemplatePath;
            // Optional retry configuration (if present in configuration section)
            if (options is EmailSenderConfiguration ext)
            {
                if (ext.MaxRetryAttempts > 0) MaxRetryAttempts = ext.MaxRetryAttempts;
                if (ext.RetryBackoffSeconds > 0) RetryBackoffSeconds = ext.RetryBackoffSeconds;
                // 2.3.0 keys: applied only when present, so an absent key keeps the default and an
                // explicit 0 turns the feature off.
                if (ext.MaxRetryBackoffSeconds != null) MaxRetryBackoffSeconds = Math.Max(0, ext.MaxRetryBackoffSeconds.Value);
                if (ext.OutagePauseThreshold != null) OutagePauseThreshold = Math.Max(0, ext.OutagePauseThreshold.Value);
                if (ext.StaleClaimMinutes != null && ext.StaleClaimMinutes.Value > 0) StaleClaimMinutes = ext.StaleClaimMinutes.Value;
                if (ext.PartitionFairness != null) PartitionFairness = ext.PartitionFairness.Value;
                if (ext.PollIntervalSeconds != null) PollIntervalSeconds = Math.Max(0, ext.PollIntervalSeconds.Value);
            }
        }
    }
}
