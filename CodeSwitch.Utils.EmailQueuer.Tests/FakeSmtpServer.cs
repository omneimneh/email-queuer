using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodeSwitch.Utils.EmailQueuer.Tests
{
    /// <summary>What one accepted connection does.</summary>
    public enum RelayBehavior
    {
        /// <summary>A plain SMTP server: greeting, EHLO (no STARTTLS, no AUTH), MAIL, RCPT, DATA, QUIT.</summary>
        Normal,
        /// <summary>Accepts the TCP connection and never sends a byte (no greeting, no reply).</summary>
        SilentFromStart,
        /// <summary>
        /// Behaves normally for the first message, then never answers again on this connection:
        /// what a connection a NAT dropped while idle looks like to the client (its MAIL FROM is
        /// written and no reply ever comes).
        /// </summary>
        SilentAfterFirstMessage
    }

    /// <summary>
    /// A minimal SMTP relay on 127.0.0.1 for the tests. Connection n (1-based) follows
    /// <c>behaviorFor(n)</c>. Silent connections are held open, never closed, until disposal.
    /// </summary>
    public sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Func<int, RelayBehavior> behaviorFor;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly List<TcpClient> clients = new List<TcpClient>();
        private int connections;
        private int messages;
        private int quits;
        private int activeData;
        private int peakData;
        private readonly ConcurrentQueue<string> recipients = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> messageData = new ConcurrentQueue<string>();

        public FakeSmtpServer(Func<int, RelayBehavior> behaviorFor)
        {
            this.behaviorFor = behaviorFor;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public int Connections => Volatile.Read(ref connections);
        public int Messages => Volatile.Read(ref messages);
        public int Quits => Volatile.Read(ref quits);
        /// <summary>Wait between the end of a message's data and the "250 queued" reply (a slow relay).</summary>
        public TimeSpan DataDelay { get; set; } = TimeSpan.Zero;
        /// <summary>Most messages whose data was being accepted at the same moment (concurrent sends).</summary>
        public int PeakConcurrentData => Volatile.Read(ref peakData);
        /// <summary>Every RCPT TO argument accepted, in arrival order, one entry per delivered message recipient.</summary>
        public string[] Recipients => recipients.ToArray();
        /// <summary>The raw DATA (headers and MIME body, dot-unstuffed) of every delivered message, in arrival order.</summary>
        public string[] MessageData => messageData.ToArray();

        private async Task AcceptLoopAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch
                {
                    return;
                }
                lock (clients) clients.Add(client);
                var n = Interlocked.Increment(ref connections);
                _ = Task.Run(() => ServeAsync(client, behaviorFor(n)));
            }
        }

        private async Task ServeAsync(TcpClient client, RelayBehavior behavior)
        {
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

                if (behavior == RelayBehavior.SilentFromStart)
                {
                    await DrainSilentlyAsync(reader);
                    return;
                }

                await writer.WriteLineAsync("220 fake.local ESMTP");
                var delivered = 0;
                while (!stop.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(stop.Token);
                    if (line == null) return;
                    var verb = line.Length >= 4 ? line.Substring(0, 4).ToUpperInvariant() : line.ToUpperInvariant();

                    if (verb == "MAIL" && behavior == RelayBehavior.SilentAfterFirstMessage && delivered >= 1)
                    {
                        await DrainSilentlyAsync(reader);
                        return;
                    }

                    switch (verb)
                    {
                        case "EHLO":
                            await writer.WriteLineAsync("250-fake.local");
                            await writer.WriteLineAsync("250 8BITMIME");
                            break;
                        case "RCPT":
                            recipients.Enqueue(line.Substring(line.IndexOf(':') + 1).Trim().Trim('<', '>'));
                            await writer.WriteLineAsync("250 OK");
                            break;
                        case "HELO":
                        case "MAIL":
                        case "RSET":
                        case "NOOP":
                            await writer.WriteLineAsync("250 OK");
                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 go ahead");
                            string data;
                            var body = new StringBuilder();
                            var active = Interlocked.Increment(ref activeData);
                            int peak;
                            while (active > (peak = Volatile.Read(ref peakData))
                                   && Interlocked.CompareExchange(ref peakData, active, peak) != peak) { }
                            try
                            {
                                while ((data = await reader.ReadLineAsync(stop.Token)) != null && data != ".")
                                    body.Append(data.StartsWith("..") ? data.Substring(1) : data).Append("\r\n");
                                if (DataDelay > TimeSpan.Zero)
                                    await Task.Delay(DataDelay, stop.Token);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref activeData);
                            }
                            delivered++;
                            messageData.Enqueue(body.ToString());
                            Interlocked.Increment(ref messages);
                            await writer.WriteLineAsync("250 queued");
                            break;
                        case "QUIT":
                            Interlocked.Increment(ref quits);
                            await writer.WriteLineAsync("221 bye");
                            client.Close();
                            return;
                        default:
                            await writer.WriteLineAsync("502 not implemented");
                            break;
                    }
                }
            }
            catch
            {
                // the client went away
            }
        }

        /// <summary>Reads (and ignores) whatever arrives, never answers, never closes.</summary>
        private async Task DrainSilentlyAsync(StreamReader reader)
        {
            try
            {
                while (!stop.IsCancellationRequested && await reader.ReadLineAsync(stop.Token) != null) { }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            lock (clients)
            {
                foreach (var c in clients)
                {
                    try { c.Close(); } catch { }
                }
            }
        }
    }
}
