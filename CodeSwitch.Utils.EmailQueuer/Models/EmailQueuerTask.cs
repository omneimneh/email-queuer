using System;

namespace CodeSwitch.Utils.EmailQueuer.Models
{
    public class EmailQueuerTask
    {
        /// <summary>
        /// Maximum length of <see cref="PartitionKey"/> (the column is nvarchar(64)).
        /// </summary>
        public const int PartitionKeyMaxLength = 64;
        /// <summary>
        /// Maximum length of <see cref="LastError"/> (the column is nvarchar(1000)); longer text is truncated.
        /// </summary>
        public const int LastErrorMaxLength = 1000;

        public int Id { get; set; }
        public string To { get; set; }
        public string CC { get; set; }
        public string BCC { get; set; }
        public string AttachmentPaths { get; set; }
        public string Subject { get; set; }
        public string ModelJson { get; set; }
        public string Template { get; set; }
        public string ModelType { get; set; }
        public EmailTaskStatus Status { get; set; }
        public DateTime CreatedOn { get; set; }
        public DateTime SentOn { get; set; }
    public int RetryCount { get; set; }
    public DateTime? NextAttemptOn { get; set; }
        /// <summary>
        /// Send order (2.3.0). Higher goes first; 0 is normal mail, a negative value is bulk mail that
        /// goes out only when nothing of a higher priority is due. Column: int NOT NULL DEFAULT 0.
        /// </summary>
        public int Priority { get; set; }
        /// <summary>
        /// Fairness key (2.3.0), opaque to the library: within one priority the worker takes turns
        /// between the distinct keys, so a burst under one key cannot hold up another key's mail.
        /// NULL is a partition of its own. Column: nvarchar(64) NULL.
        /// </summary>
        public string PartitionKey { get; set; }
        /// <summary>
        /// Why the last attempt failed (2.3.0): exception type and message, up to three inner
        /// exceptions and the SMTP status code, no stack trace. Cleared when the email is sent.
        /// May quote a recipient address (SMTP rejections do), so treat it like <see cref="To"/>.
        /// Column: nvarchar(1000) NULL.
        /// </summary>
        public string LastError { get; set; }
        /// <summary>
        /// When (UTC) the last attempt failed (2.3.0). Cleared when the email is sent. Column: datetime2 NULL.
        /// </summary>
        public DateTime? LastErrorOn { get; set; }
    }
}
