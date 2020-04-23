using System;

namespace EmailQueuer.Models
{
    public class EmailQueuerTask
    {
        public int Id { get; set; }
        public string To { get; set; }
        public string Subject { get; set; }
        public string ModelJson { get; set; }
        public string Template { get; set; }
        public string ModelType { get; set; }
        public EmailTaskStatus Status { get; set; }
        public DateTime CreatedOn { get; set; }
        public DateTime SentOn { get; set; }
    }
}
