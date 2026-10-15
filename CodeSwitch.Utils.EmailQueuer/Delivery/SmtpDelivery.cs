using FluentEmail.Core.Models;
using System;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodeSwitch.Utils.EmailQueuer.Delivery
{
    /// <summary>
    /// Sends the queue's emails over SMTP with a hard bound on every send (2.3.0-rc.2).
    ///
    /// Why the library no longer hands the message to FluentEmail's SmtpSender:
    /// <list type="bullet">
    /// <item>FluentEmail.Smtp 3.0.2 drops the cancellation token it is given, so a cancelled send
    /// keeps running.</item>
    /// <item>System.Net.Mail.SmtpClient applies <see cref="SmtpClient.Timeout"/> to the synchronous
    /// Send only. The asynchronous send has no timeout at all.</item>
    /// <item>The SmtpClient that AddSmtpSender registers is a singleton, and an asynchronous send
    /// that succeeds never closes its connection (only Dispose sends QUIT). The next send writes
    /// MAIL FROM onto that connection however long it has been idle. A connection a NAT or the
    /// relay dropped silently while idle never answers: the send waited until the operating system
    /// gave up on the TCP connection, about 16 minutes, and held the whole queue for that long.</item>
    /// </list>
    ///
    /// So one instance of this class owns one SmtpClient for the length of one queue pass. A
    /// connection is reused only while it is fresh (a few seconds between sends, a bounded number
    /// of messages); otherwise it is closed with QUIT and a new one is opened. Every send runs under
    /// a timeout that covers connect, TLS, AUTH and the whole SMTP exchange; when it fires, the
    /// connection is aborted and the send fails with a <see cref="TimeoutException"/>. Not thread
    /// safe: one send at a time, which is how the worker uses it.
    /// </summary>
    internal sealed class SmtpDelivery : IDisposable
    {
        /// <summary>Extra wait after the timeout for the aborted send to unwind before it is abandoned.</summary>
        private static readonly TimeSpan AbortGrace = TimeSpan.FromSeconds(5);

        private readonly SmtpClient template;
        private readonly TimeSpan sendTimeout;
        private readonly TimeSpan maxIdle;
        private readonly int maxMessagesPerConnection;

        private SmtpClient client;
        private DateTime lastSentUtc;
        private int sentOnClient;
        private bool disposed;

        public SmtpDelivery(SmtpClient template, TimeSpan sendTimeout, TimeSpan maxIdle, int maxMessagesPerConnection)
        {
            this.template = template ?? throw new ArgumentNullException(nameof(template));
            this.sendTimeout = sendTimeout;
            this.maxIdle = maxIdle;
            this.maxMessagesPerConnection = Math.Max(1, maxMessagesPerConnection);
        }

        /// <summary>Number of SmtpClient instances (and so connections) this delivery has opened.</summary>
        internal int ClientsCreated { get; private set; }

        public TimeSpan SendTimeout => sendTimeout;

        /// <summary>
        /// Sends <paramref name="data"/>. Returns when the server accepted the message; throws the
        /// SMTP or I/O failure otherwise, or a <see cref="TimeoutException"/> if the exchange did not
        /// end within the send timeout.
        /// </summary>
        public async Task SendAsync(EmailData data)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(SmtpDelivery));

            // Build first: a message that cannot be built is the message's fault and must not cost
            // the connection.
            var message = CreateMailMessage(data);

            if (client != null && (DateTime.UtcNow - lastSentUtc > maxIdle || sentOnClient >= maxMessagesPerConnection))
                CloseClient();
            if (client == null)
            {
                client = CreateClient(template);
                ClientsCreated++;
                sentOnClient = 0;
            }

            var current = client;
            using var timeout = new CancellationTokenSource();
            Task send;
            try
            {
                send = current.SendMailAsync(message, timeout.Token);
            }
            catch
            {
                message.Dispose();
                AbandonOrClose(current, completed: true);
                throw;
            }

            // The token makes SmtpClient abort the connection (SendAsyncCancel), which ends the
            // pending connect, read or write. The extra grace bounds the wait even if an abort ever
            // failed to unwind the send: past it the client is abandoned, never awaited.
            timeout.CancelAfter(sendTimeout);
            var finished = await Task.WhenAny(send, Task.Delay(sendTimeout + AbortGrace)).ConfigureAwait(false);

            if (finished != send)
            {
                // Still running: leave it to finish on its own, observe its outcome, never reuse it.
                _ = send.ContinueWith(t => { _ = t.Exception; message.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                AbandonOrClose(current, completed: false);
                throw TimedOut(null);
            }

            try
            {
                await send.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                message.Dispose();
                // A failed SmtpClient has already aborted its connection; start the next send on a new one.
                AbandonOrClose(current, completed: true);
                if (timeout.IsCancellationRequested)
                    throw TimedOut(ex);
                throw;
            }

            message.Dispose();
            lastSentUtc = DateTime.UtcNow;
            sentOnClient++;
        }

        private TimeoutException TimedOut(Exception inner)
        {
            var text = $"The SMTP send to {template.Host}:{template.Port} did not complete within {(long)sendTimeout.TotalSeconds} seconds; the connection was aborted";
            return inner == null ? new TimeoutException(text) : new TimeoutException(text, inner);
        }

        private void AbandonOrClose(SmtpClient failed, bool completed)
        {
            if (ReferenceEquals(client, failed))
                client = null;
            if (!completed)
                return; // Disposing a client in a call aborts it; it is already aborted. Let it go.
            try { failed.Dispose(); } catch { /* the connection is gone either way */ }
        }

        private void CloseClient()
        {
            var old = client;
            client = null;
            if (old == null)
                return;
            // Dispose sends QUIT (non-blocking socket) and closes the connection.
            try { old.Dispose(); } catch { /* best effort: the connection is gone either way */ }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            CloseClient();
        }

        /// <summary>
        /// A new SmtpClient with the settings of <paramref name="source"/> (the one the host
        /// configured in <see cref="Options.EmailQueuerOptions.SmtpClient"/>, which is never used
        /// to send).
        /// </summary>
        internal static SmtpClient CreateClient(SmtpClient source)
        {
            var c = new SmtpClient();
            c.Host = source.Host;
            if (source.Port > 0) c.Port = source.Port;
            c.EnableSsl = source.EnableSsl;
            c.DeliveryMethod = source.DeliveryMethod;
            c.DeliveryFormat = source.DeliveryFormat;
            if (source.PickupDirectoryLocation != null) c.PickupDirectoryLocation = source.PickupDirectoryLocation;
            c.Timeout = source.Timeout;
            if (source.TargetName != null) c.TargetName = source.TargetName;
            // UseDefaultCredentials replaces Credentials when set, so set exactly one of the two.
            if (source.UseDefaultCredentials)
                c.UseDefaultCredentials = true;
            else
                c.Credentials = source.Credentials;
            if (source.ClientCertificates.Count > 0)
                c.ClientCertificates.AddRange(source.ClientCertificates);
            return c;
        }

        /// <summary>
        /// The MailMessage FluentEmail.Smtp 3.0.2 (SmtpSender.CreateMailMessage) builds for the same
        /// email, so moving off SmtpSender changes nothing in what is sent.
        /// </summary>
        internal static MailMessage CreateMailMessage(EmailData data)
        {
            MailMessage message;

            if (!string.IsNullOrEmpty(data.PlaintextAlternativeBody))
            {
                message = new MailMessage
                {
                    Subject = data.Subject,
                    Body = data.PlaintextAlternativeBody,
                    IsBodyHtml = false,
                    From = new MailAddress(data.FromAddress.EmailAddress, data.FromAddress.Name)
                };
                var mimeType = new System.Net.Mime.ContentType("text/html; charset=UTF-8");
                message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(data.Body, mimeType));
            }
            else
            {
                message = new MailMessage
                {
                    Subject = data.Subject,
                    Body = data.Body,
                    IsBodyHtml = data.IsHtml,
                    BodyEncoding = Encoding.UTF8,
                    SubjectEncoding = Encoding.UTF8,
                    From = new MailAddress(data.FromAddress.EmailAddress, data.FromAddress.Name)
                };
            }

            if (data.Headers != null)
                foreach (var header in data.Headers)
                    message.Headers.Add(header.Key, header.Value);

            foreach (var x in data.ToAddresses) message.To.Add(new MailAddress(x.EmailAddress, x.Name));
            foreach (var x in data.CcAddresses) message.CC.Add(new MailAddress(x.EmailAddress, x.Name));
            foreach (var x in data.BccAddresses) message.Bcc.Add(new MailAddress(x.EmailAddress, x.Name));
            foreach (var x in data.ReplyToAddresses) message.ReplyToList.Add(new MailAddress(x.EmailAddress, x.Name));

            switch (data.Priority)
            {
                case Priority.Low: message.Priority = MailPriority.Low; break;
                case Priority.Normal: message.Priority = MailPriority.Normal; break;
                case Priority.High: message.Priority = MailPriority.High; break;
            }

            foreach (var x in data.Attachments)
            {
                var a = new System.Net.Mail.Attachment(x.Data, x.Filename, x.ContentType);
                a.ContentId = x.ContentId;
                message.Attachments.Add(a);
            }

            return message;
        }
    }
}
