using System.Dynamic;

namespace EmailQueuer.Options
{
    public class EmailSenderConfiguration
    {
        public SenderConfiguration Sender { get; set; }
        public SmtpConfiguration Smtp { get; set; }
        public ExpandoObject ViewBag { get; set; }
        public bool MoveCssInline { get; set; }
    }
}
