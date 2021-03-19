using CodeSwitch.Utils.EmailQueuer.Models;
using CodeSwitch.Utils.EmailQueuer.Options;
using FluentEmail.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RazorLight;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodeSwitch.Utils.EmailQueuer
{
    public class EmailQueuer<TContext> : BackgroundService where TContext : DbContext, IEmailQueuerContext
    {
        private readonly IServiceScopeFactory factory;
        private readonly EmailQueuerOptions options;
        private readonly ILogger<EmailQueuer<TContext>> logger;
        private bool isLooping = false;

        public EmailQueuer(IServiceScopeFactory factory, EmailQueuerOptions options,
            ILogger<EmailQueuer<TContext>> logger)
        {
            this.factory = factory;
            this.options = options;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await StartLoopAsync();
        }

        private async Task StartLoopAsync()
        {
            isLooping = true;
            await LoopAsync();
            isLooping = false;
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
            using var scope = factory.CreateScope();
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
                Status = EmailTaskStatus.Pending
            });
            await context.SaveChangesAsync();

            if (!isLooping)
            {
                _ = StartLoopAsync();
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
            await SendEmailAsync(emailTask, fluent);
        }

        private async Task SendEmailAsync(EmailQueuerTask emailTask, IFluentEmailFactory fluent)
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

            var attachmentFileNames = emailTask.AttachmentPaths.Split(";");

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

            await emailToSend.SendAsync();
        }

        private async Task<string> GenerateEmailBody(string template, object model)
        {
            var engine = new RazorLightEngineBuilder().UseEmbeddedResourcesProject(options.Assembly).Build();

            dynamic viewBag = options.ViewBag;
            viewBag.Subject = template.ToString();

            string html = await engine.CompileRenderAsync(string.Format(options.TemplatePath, template), model, viewBag);
            if (options.MoveCssInline)
            {
                using var preMailer = new PreMailer.Net.PreMailer(html);
                html = preMailer.MoveCssInline().Html;
            }
            return html;
        }

        private async Task LoopAsync()
        {
            using var scope = factory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var fluent = scope.ServiceProvider.GetRequiredService<IFluentEmailFactory>();

            while (context.EmailQueuerTasks.Any(x => x.Status == EmailTaskStatus.Pending))
            {
                var emailTask = await context.EmailQueuerTasks
                    .Where(x => x.Status == EmailTaskStatus.Pending)
                    .OrderBy(x => x.CreatedOn).FirstOrDefaultAsync();

                logger.LogInformation($"Sending email to {emailTask.To}");

                try
                {
                    emailTask.Status = EmailTaskStatus.Sending;
                    await context.SaveChangesAsync();

                    await SendEmailAsync(emailTask, fluent);
                    logger.LogInformation("Email sent successfully");

                    emailTask.Status = EmailTaskStatus.Sent;
                    await context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error while sending email");

                    emailTask.Status = EmailTaskStatus.Error;
                    await context.SaveChangesAsync();
                }
            }
        }
    }
}
