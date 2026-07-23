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
using System.Threading;
using System.Threading.Tasks;
using Razor.Templating.Core;

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

            if (options.RunHostedService && !isLooping)
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

        private async Task LoopAsync()
        {
            using var scope = factory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            var fluent = scope.ServiceProvider.GetRequiredService<IFluentEmailFactory>();

            // Process all tasks that are currently due (ready now)
            while (true)
            {
                var now = DateTime.UtcNow;

                // Stale-claim reclaim (§2-E.3): a row left in Sending past its stamped claim
                // deadline (worker died mid-send, e.g. slot killed during a swap) is otherwise
                // orphaned forever — the candidate query below only selects Pending|Error.
                // Two arms, both reusing EXISTING nullable columns (NO schema change):
                //   1. NextAttemptOn stamped (claimed by 2.2.0+ code) and past the deadline.
                //   2. NextAttemptOn NULL (row moved to Sending by pre-2.2.0 code, which never
                //      stamped a deadline) and older than the StaleClaimMinutes window by
                //      CreatedOn — without this arm such legacy rows stay orphaned forever.
                var legacyCutoff = now.AddMinutes(-options.StaleClaimMinutes);
                await context.EmailQueuerTasks
                    .Where(x => x.Status == EmailTaskStatus.Sending
                                && ((x.NextAttemptOn != null && x.NextAttemptOn <= now)
                                    || (x.NextAttemptOn == null && x.CreatedOn <= legacyCutoff)))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, EmailTaskStatus.Error));

                var emailTask = await context.EmailQueuerTasks
                    .Where(x => (x.Status == EmailTaskStatus.Pending || x.Status == EmailTaskStatus.Error)
                                && (x.NextAttemptOn == null || x.NextAttemptOn <= now))
                    .OrderBy(x => x.CreatedOn)
                    .FirstOrDefaultAsync();

                if (emailTask == null)
                {
                    // Nothing due right now. If there are future scheduled retries, schedule a delayed restart.
                    var nextRetryTime = await context.EmailQueuerTasks
                        .Where(x => (x.Status == EmailTaskStatus.Pending || x.Status == EmailTaskStatus.Error)
                                    && x.NextAttemptOn != null)
                        .OrderBy(x => x.NextAttemptOn)
                        .Select(x => x.NextAttemptOn)
                        .FirstOrDefaultAsync();

                    if (nextRetryTime != null)
                    {
                        var delay = nextRetryTime.Value - DateTime.UtcNow;
                        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                        logger.LogDebug($"Scheduling background restart in {delay.TotalSeconds:F0}s for next retry batch");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(delay);
                                if (!isLooping)
                                {
                                    _ = StartLoopAsync();
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Error while scheduling next retry loop");
                            }
                        });
                    }
                    break; // Exit to release resources; a restart will occur when needed.
                }

                // Atomic claim (§2-E.1): CAS Pending/Error -> Sending only if the row is still in
                // the status we read. Stamp NextAttemptOn with the claim deadline so the reclaim
                // above can recover it if this worker dies mid-send. 0 rows => another instance won.
                var seenStatus = emailTask.Status;
                var claimDeadline = now.AddMinutes(options.StaleClaimMinutes);
                var claimed = await context.EmailQueuerTasks
                    .Where(x => x.Id == emailTask.Id && x.Status == seenStatus)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Status, EmailTaskStatus.Sending)
                        .SetProperty(x => x.NextAttemptOn, claimDeadline));
                if (claimed == 0)
                {
                    context.Entry(emailTask).State = EntityState.Detached;
                    continue; // lost the race; move to next candidate
                }
                // Re-baseline the tracked entity so the later SaveChanges (Sent / Error) issues a
                // correct UPDATE against the just-claimed DB state.
                emailTask.Status = EmailTaskStatus.Sending;
                emailTask.NextAttemptOn = claimDeadline;
                context.Entry(emailTask).State = EntityState.Unchanged;

                logger.LogInformation($"Sending email (Retry {emailTask.RetryCount}) to {emailTask.To}");

                try
                {
                    await SendEmailAsync(emailTask, fluent);
                    logger.LogInformation("Email sent successfully");

                    emailTask.Status = EmailTaskStatus.Sent;
                    emailTask.SentOn = DateTime.UtcNow;
                    await context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error while sending email");

                    emailTask.RetryCount++;
                    if (emailTask.RetryCount > options.MaxRetryAttempts)
                    {
                        emailTask.Status = EmailTaskStatus.PermanentlyFailed;
                        logger.LogWarning($"Email task {emailTask.Id} permanently failed after {options.MaxRetryAttempts} attempts");
                    }
                    else
                    {
                        emailTask.Status = EmailTaskStatus.Error;
                        var delaySeconds = options.RetryBackoffSeconds * (int)Math.Pow(2, Math.Max(0, emailTask.RetryCount - 1));
                        emailTask.NextAttemptOn = DateTime.UtcNow.AddSeconds(delaySeconds);
                        logger.LogInformation($"Scheduling retry #{emailTask.RetryCount} for task {emailTask.Id} in {delaySeconds} seconds");
                    }
                    await context.SaveChangesAsync();
                }
            }
        }
    }
}
