using System.Dynamic;

namespace CodeSwitch.Utils.EmailQueuer.Options
{
    public class EmailSenderConfiguration
    {
        public SenderConfiguration Sender { get; set; }
        public SmtpConfiguration Smtp { get; set; }
        public ExpandoObject ViewBag { get; set; }
        public bool MoveCssInline { get; set; }
        public string TemplatePath { get; set; }
    public int MaxRetryAttempts { get; set; } = 3;
    public int RetryBackoffSeconds { get; set; } = 30;
        // 2.3.0 keys. Nullable so that an absent key leaves the option at its default while an
        // explicit 0 (which means "off" for most of them) is still honoured.
        public int? MaxRetryBackoffSeconds { get; set; }
        public int? OutagePauseThreshold { get; set; }
        public int? StaleClaimMinutes { get; set; }
        public bool? PartitionFairness { get; set; }
        public int? PollIntervalSeconds { get; set; }
        // 2.3.0-rc.2
        public int? SendTimeoutSeconds { get; set; }
    }
}
