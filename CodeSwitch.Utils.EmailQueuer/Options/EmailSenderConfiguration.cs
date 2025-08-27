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
    }
}
