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
            ViewBag = new ExpandoObject()
        };

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
        }
    }
}