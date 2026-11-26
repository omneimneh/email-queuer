# Email Queuer
A library for ASP.NET Core (.NET 8 and .NET 10) that allows you to send emails without memory leaks and in a very clean way.

Features:
- Background email queue persisted to your database (no in-memory growth / leaks)
- Automatic retry with exponential backoff (configurable, with an optional ceiling) and permanent failure cut-off
- Saved status, metadata and the reason of the last failure so you can inspect / manually resend failed emails
- Priorities, and fair turns between tenants (partition keys), so bulk mail cannot hold up urgent mail
- Optional outage pause: a mail server outage does not burn the retry budget of every queued email
- Razor templating engine support: RazorLight (version < 1.0.5.3) or Razor.Templating.Core (>= 2.0.0)
- Global CSS moved inline for email client compatibility (optional)
- Configurable template path & dynamic ViewBag

New in >= 2.1.0:
- Target framework upgraded to net8.0
- Retry logic with backoff (RetryCount, NextAttemptOn, MaxRetryAttempts, RetryBackoffSeconds)
- New status: `PermanentlyFailed` once retries are exhausted
- Additional task fields: `RetryCount`, `NextAttemptOn`, `SentOn`

New in 2.5.0 (no schema change, see [Upgrading to 2.5.0](#upgrading-to-250)):
- Runs on .NET 10: the package ships `lib/net8.0` (EF Core 8) and `lib/net10.0` (EF Core 10). The 2.4.0 build fails on EF Core 10, whose `ExecuteUpdateAsync` takes a different setter type
- `SendDirectAsync(IFluentEmail, CancellationToken)`: send an email you built yourself now, through the same sender and send timeout as the queue, without a queue row (see [Sending a ready-made email now](#sending-a-ready-made-email-now-250))
- Inline images: an attachment marked `IsInline` with a `ContentId` is sent as part of the HTML body, so `<img src="cid:...">` renders
- `FinishedModelScrubber`: remove secrets such as a starting password from the stored model once the email is finished (see [Removing sensitive values after sending](#removing-sensitive-values-after-sending-250))
- PreMailer.Net 2.7.4 (AngleSharp 1.7.2, clear of GHSA-pgww-w46g-26qg); the CSS moved inline is byte for byte the same as before on the library's test templates
- Razor.Templating.Core 2.0.0
- On .NET 10, `SmtpClient.Timeout` also applies to asynchronous sends; the queue keeps `SendTimeoutSeconds` as the only bound on its sends
- The package carries XML documentation and embedded symbols

New in 2.4.0 (no schema change, see [Sending several emails at once](#sending-several-emails-at-once-240)):
- `MaxConcurrentSends` (default 1, the 2.3.0 behaviour): a worker can send up to N emails at the same time, each on its own SMTP connection, with priority, turns, the claim, the retry budget, the stale-claim reclaim and the send timeout unchanged

New in 2.3.0 (**schema change**, see [Upgrading to 2.3.0](#upgrading-to-230)):
- `Priority` and `PartitionKey` on each email, set through a new `EnqueueAsync` overload taking `EmailEnqueueOptions`
- `LastError` and `LastErrorOn` on each email: why and when the last attempt failed
- `MaxRetryBackoffSeconds` caps one retry wait; the wait can no longer overflow into an immediate-retry loop
- `OutagePauseThreshold` pauses the queue during a mail server outage instead of failing every email in it
- `PollIntervalSeconds` (default 120): the worker also wakes periodically, so an email made due outside the process (another instance, an admin screen, SQL) no longer waits for the next enqueue
- The worker can no longer wedge after an exception or run two loops at once; a stale claim counts as a failed attempt
- Every send is bounded by `SendTimeoutSeconds` (default 60), connect and TLS included, and an SMTP connection is never reused after 30 seconds idle. A send that hangs fails, records a `TimeoutException` in `LastError` and waits its retry backoff; the queue moves on at once
- `StaleClaimMinutes` can be set from configuration

## How to use
Follow these steps to get your email queuer up and running

#### Install package from Nuget
Run the following command in your package manager console: `Install-Package CodeSwitch.Utils.EmailQueuer`

#### Add your Razor templates
Create a folder for your templates, then create an empty class in that folder, let's call it `Mails.cs`
Add your `.cshtml` files to this folder (you can also put them in subfolders)
Add the following to your `.csproj`:
```xml
  <PropertyGroup>
    <!-- Other Properties -->
    <PreserveCompilationReferences>true</PreserveCompilationReferences>
  </PropertyGroup>
  
  <!-- Other sections -->
  
  <ItemGroup>
    <EmbeddedResource Include="FolderContainingRazorTemplates\**" />
  </ItemGroup>
```
#### Add database table
The email queuer main objective is to avoid memory leaks, so the email queue should be stored in the database. Your `DbContext` should implement `IEmailQueuerContext` like the following:

```csharp
public class AppDbContext : DbContext, IEmailQueuerContext
{
    public AppDbContext(DbContextOptions options) : base(options) { }
   
    // Add the required table
    public virtual DbSet<EmailQueuerTask> EmailQueuerTasks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // This currently doesn't do much but but may be required in future versions so make sure to call it
        (this as IEmailQueuerContext).Initialize(modelBuilder);
    }
}
```

#### Adding service to the dependency inejction container
Now it's time to edit the `Startup.cs`:
In the `ConfigureServices` method add the following:
```csharp
services.AddEmailQueuer<AppDbContext>(typeof(Mail), options =>
{
    // default section name is "EmailQueuer"
    options.LoadFromConfiguration(configuration /*, Section name in appsettings.json */);
});
```

This is how your `appsettings.json` should look, replace `<SectionName>` with the one you used above. (New retry settings are optional.)
```json
"<SectionName>": {
    "Sender": {
      "Email": "sender@example.com",
      "Password": "123456"
    },
    "Smtp": {
      "Timeout": 20,
      "Host": "smtp.gmail.com",
      "Port":  587
    },
    "ViewBag": {
      "WebsiteLink": "https://github.com/omneimneh/email-queuer"
    },
    "MoveCssInline": true,
  "TemplatePath": "/FolderContainingRazorTemplates/{0}.cshtml",
  "MaxRetryAttempts": 3,
  "RetryBackoffSeconds": 30,
  "MaxRetryBackoffSeconds": 0,
  "OutagePauseThreshold": 0,
  "StaleClaimMinutes": 10,
  "PartitionFairness": true,
  "PollIntervalSeconds": 120,
  "MaxConcurrentSends": 1
  }
```
The values shown for the retry and queue keys are the defaults; every one of them is optional. They are described under [Configuration keys](#configuration-keys).

You can also configure them manually 
*Note that it's not recommended to put your credentials in the source code*
```csharp
options.Sender.Password = "123456";
options.SmtpClient.Host = "smtp.gmail.com";
options.SmtpClient.Port = 587;
options.SmtpClient.EnableSsl = true;
options.SmtpClient.Timeout = 20;
options.ViewBag.WebsiteLink = "https://github.com/omneimneh/email-queuer";
options.MoveCssInline = true;
options.MaxRetryAttempts = 5;            // optional override (default 3)
options.RetryBackoffSeconds = 45;        // optional override (default 30)
options.MaxRetryBackoffSeconds = 900;    // optional ceiling on one wait (default 0 = none)
options.OutagePauseThreshold = 5;        // optional outage pause (default 0 = off)
options.PollIntervalSeconds = 120;       // periodic wake (default 120, 0 = off)
options.MaxConcurrentSends = 1;          // emails sent at once (default 1, at most 16)
```

#### Use the email sender in your services or controllers
You're basically ready to go, all you need to do is inject the `EmailQueuer` in your service ro controller.
```csharp
private readonly EmailQueuer<AppDbContext> emailQueuer;

public HomeController(EmailQueuer<AppDbContext> emailQueuer)
{
    this.emailQueuer = emailQueuer;
}
```

Now call the `EnqueueAsync` method with the following args:
* Receiver email, or `;` seperated list of emails, or array of emails
* Subject of the email
* Email template name, for example "Welcome", **Note that** this will be replaced by: `string.Format(TemplatePath, "Welcome")`, your template path is by default `{0}.cshtml`, you can also configure it in the startup `AddEmailQueuer` extenison method
* The model, it is important that it can be serializable and using the same class used in the Razor template file `Welcome.cshtml`.
```csharp
await emailQueuer.EnqueueAsync(email, "Example", "Welcome", new Person
{
    FirstName = "FName",
    LastName = "LName"
});
```
And congrats, your email will be sent in the background with no additional effort.

#### Priority and fair turns (2.3.0)
Pass an `EmailEnqueueOptions` as the fifth argument to give an email a priority, a partition key, or both. It also carries the CC, BCC and attachment paths of the older overloads.
```csharp
await emailQueuer.EnqueueAsync(email, "Weekly digest", "Digest", model, new EmailEnqueueOptions
{
    Priority = -10,              // higher goes first; 0 is the default; negative = bulk
    PartitionKey = "tenant:42"   // opaque to the library, at most 64 characters
});
```
* **Priority.** The worker always sends the highest priority that has due mail. An email queued with the older overloads has priority 0, so anything queued with a negative priority goes out after it.
* **Partition key.** Within one priority the worker takes turns between the distinct keys: with 586 emails queued under key A and 3 under key B, the order is A, B, A, B, A, B, A, A... Emails with no key are one partition of their own. Nothing is rate limited, dropped or merged: when only one key has mail it is sent at full speed. Set `PartitionFairness` to `false` to send strictly oldest first within a priority.
* The turn order is kept in memory per worker process: exact with one worker, approximate with several.
* The worker loads up to 10 emails at a time, so an email that arrives with a higher priority (or under a new key) waits for at most the 10 already loaded.
* A partition key longer than 64 characters is rejected with an `ArgumentException` at enqueue time.
* The existing overloads are unchanged and keep queuing normal-priority mail with no partition key. One source-level caveat: a call that passes a literal `null` as the fifth argument is now ambiguous between `string cc` and `EmailEnqueueOptions`; write `cc: null` or `(string)null`.

#### Sending a ready-made email now (2.5.0)
`SendDirectAsync` sends an email you built yourself, at once, for what a queue row cannot carry: per-message headers (`List-Unsubscribe` for instance) and inline images. `AddEmailQueuer` registers `IFluentEmailFactory`, so create the email with it.
```csharp
var email = fluentEmailFactory.Create()
    .To("someone@example.com")
    .Subject("What's new")
    .Body(html, true)                  // contains <img src="cid:logo">
    .PlaintextAlternativeBody(text);
email.Header("List-Unsubscribe", "<https://example.com/unsubscribe>");
email.Attach(new FluentEmail.Core.Models.Attachment
{
    Data = new MemoryStream(logoBytes),
    Filename = "logo.png",
    ContentType = "image/png",
    ContentId = "logo",
    IsInline = true
});
await emailQueuer.SendDirectAsync(email);
```
* No queue row is written, so there is no retry, priority, turn or outage pause. The caller owns retries and its own record of what was sent.
* Build a new email for every attempt: the send disposes the email's attachment streams, so sending the same `IFluentEmail` twice fails.
* Create the email with `IFluentEmailFactory`. An email made with `new Email(...)` carries FluentEmail's default sender, which saves the message to the root of the disk; `SendDirectAsync` refuses it (and an email with no sender) with `InvalidOperationException`.
* The email goes through the same sender as the queue: the built-in SMTP path on a connection of its own, closed with `QUIT`, or your `ISender`. The send is bounded by `SendTimeoutSeconds`, and by the optional `CancellationToken`, which aborts an SMTP send like a timeout does.
* It throws `TimeoutException` when the send did not finish in time and `OperationCanceledException` when the token stopped it (either way the email may or may not have been delivered), the `SmtpException` or `IOException` of a transport failure, and `InvalidOperationException` when your `ISender` reports a failure.
* **Inline images.** With the built-in SMTP path, an attachment with `IsInline = true` and a `ContentId` on an HTML email is sent as a linked resource of the HTML body (`multipart/related`), so `<img src="cid:logo">` shows the image. With a plain-text alternative, the HTML part carries it. An inline attachment without a `ContentId`, or on a text-only email, is a normal attachment. Every other attachment is sent as before. A `ContentId` containing a control character (a line break for instance) is refused with `ArgumentException`, since System.Net.Mail would write it into the part's headers as is. A custom `ISender` handles attachments its own way.

#### Removing sensitive values after sending (2.5.0)
A queued email's model is stored as JSON in `ModelJson` and stays there after the email is sent. When a model carries a secret the email needs only for rendering (a starting password, a password-reset link, a one-time code), set `FinishedModelScrubber` to remove it once the row is finished:

```csharp
options.FinishedModelScrubber = MailModelScrubber.Scrub;
```
```csharp
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;

public static class MailModelScrubber
{
    public static string Scrub(string template, string modelJson)
    {
        var reader = new JsonTextReader(new StringReader(modelJson)) { DateParseHandling = DateParseHandling.None };
        if (JToken.ReadFrom(reader) is not JObject model || !model.Remove("Password"))
            return modelJson;
        return model.ToString(Formatting.None);
    }
}
```

* It is called once, when a row ends `Sent`, or `PermanentlyFailed` after its last attempt, with the row's template name and its `ModelJson`. What it returns is saved by the same UPDATE that records the status: no second write, and no moment where the status is final but the secret is still stored. A row that will be retried keeps its whole model, because the retry renders the email again.
* The email is rendered from the full model before the scrub; only the stored copy loses the value.
* Returning `null` or the string unchanged keeps the model. If it throws, the worker logs a warning with the task id and the exception type only (a JSON parser's message can quote the model), keeps the model and still records the status, so the scrubber can never cause a second send.
* With `MaxConcurrentSends` above 1 it is called from several sends at once: keep it thread safe and quick, with no I/O.
* The keys are the ones `JsonConvert` wrote at enqueue time: the property names as declared, unless you changed `JsonConvert.DefaultSettings`.
* Not covered: rows the stale-claim reclaim retires to `PermanentlyFailed` (one bulk UPDATE, no row is loaded), and rows that finished before the option was set. Clean those with a job of your own, on `Status` 2 (Sent) and 4 (PermanentlyFailed).
* A scrubbed row sent again with `ManuallySendEmailAsync`, or set back to `Pending`, renders without the removed values.

### Retry & Failure Handling
Each task moves through these statuses:
- `Pending` -> queued and not yet processed
- `Sending` -> actively being sent
- `Sent` -> delivered successfully
- `Error` -> last attempt failed; will be retried while `RetryCount <= MaxRetryAttempts`
- `PermanentlyFailed` -> all retry attempts exhausted (no further automatic retries); `LastError` holds the last failure

Backoff formula: `nextDelay = min(MaxRetryBackoffSeconds, RetryBackoffSeconds * 2^(RetryCount-1))`. With `MaxRetryBackoffSeconds = 0` there is no configured ceiling and a fixed ceiling of 24 hours applies. The wait is computed without ever exceeding the ceiling, so it cannot overflow whatever the retry count (up to 2.2.1 it overflowed at about 27 attempts with a 60 second base and retried in a tight loop).

**The retry budget.** `MaxRetryAttempts = N` means N waits and N + 1 sends: a row that never succeeds ends `PermanentlyFailed` with `RetryCount = N + 1`. The time budget of one email is the sum of its N waits. Examples:

| `RetryBackoffSeconds` | `MaxRetryBackoffSeconds` | `MaxRetryAttempts` | Waits | Budget |
|---|---|---|---|---|
| 30 | 0 | 3 (defaults) | 30, 60, 120 s | 3.5 minutes |
| 60 | 900 | 11 | 60, 120, 240, 480 s, then 7 x 900 s | 2 hours |
| 60 | 900 | 27 | 60, 120, 240, 480 s, then 23 x 900 s | 6 hours |

**The failure on the row (2.3.0).** Every failed attempt writes `LastError` (exception type and message, up to three inner exceptions, the SMTP status code when there is one; no stack trace; at most 1000 characters) and `LastErrorOn` (UTC). Both are cleared when the email is finally sent. An SMTP rejection usually quotes the recipient address, so treat `LastError` like the `To` column when you show it.

**Outage pause (2.3.0, off by default).** Without it, every email queued during a mail server outage spends its own retry budget against the same dead server. With `OutagePauseThreshold = N`, once N different emails have failed on transport (the server unreachable, closing the connection, refusing the session, a protocol violation, a TLS failure, a timeout) with no successful send in between, the worker stops draining, waits `RetryBackoffSeconds`, then tries a single email as a probe. Each further failed probe doubles the wait up to `MaxRetryBackoffSeconds` (15 minutes when that is 0); the first success resumes the drain in the normal order. Emails that were not attempted keep their whole budget; the emails used as probes spend theirs, one attempt per probe. A failure that is the message's own fault (model, template, bad address, missing attachment, recipient rejected) never counts towards the pause.

- **An email counts once.** Retries of the same email add nothing to the count, so one message that keeps failing never pauses the queue. This matters because a server that refuses a single message for its size or content raises the same error type as a server that is down. The other side of it: with fewer than N emails failing, the pause does not start and each of them spends its own budget.
- **Which email is the probe.** Not the oldest. Among everything that is due, whatever its priority or partition: the email with the fewest failed attempts, then the higher priority, then the newest; and an email that has not yet failed in this outage before one that has (chosen among the 10 best by that order). So mail queued behind a few messages the server keeps refusing is tried first, gets through, and ends the pause, instead of waiting for those messages to use up their budget. In a real outage the probes are spread over different emails. A probe is one email sent out of the normal order; it costs the same two reads as a normal batch.
- **Forgetting.** The count and the pause length are cleared by the first successful send, and also when no transport failure has been seen for the longest pause (at least 5 minutes) after the last pause ended.
- **Limit.** While several different messages are being refused and nothing else is sent, new mail can wait for the pause in progress, at most `MaxRetryBackoffSeconds`, before it is tried.

**Waking up.** The worker runs a pass when the process starts, when `EnqueueAsync` is called in the same process, when the next scheduled retry is due, and (2.3.0) every `PollIntervalSeconds`. The periodic wake is what picks up an email made due by something the process cannot see: another instance, an admin screen re-arming a failed row, a SQL insert. A pass that fails (a database error for instance) no longer stops the worker: it is retried 120 seconds later.

**Stale claims.** A row left in `Sending` for longer than `StaleClaimMinutes` (the worker died mid-send) goes back to `Error`. Since 2.3.0 that counts as a failed attempt: `RetryCount` is incremented, `LastError` says the claim expired, and a row already at the end of its budget becomes `PermanentlyFailed`. A send that hangs never leaves its row in `Sending`: it fails after `SendTimeoutSeconds`.

**Hung sends (2.3.0).** Every send runs under `SendTimeoutSeconds` (default 60, floor 5, at most 80% of `StaleClaimMinutes`). The bound covers the whole exchange: connecting, TLS, AUTH and the message. When it fires, the connection is aborted, the attempt is recorded like any other failure (`LastError` = `TimeoutException: The SMTP send to host:port did not complete within N seconds...`, `NextAttemptOn` = now + the retry backoff), and the worker sends the next email. `SmtpClient.Timeout` does not do this: .NET 8 applies it to the synchronous `Send` only, and FluentEmail.Smtp 3.0.2 ignores the cancellation token it is given. .NET 10 applies `SmtpClient.Timeout` (milliseconds) to asynchronous sends too; since 2.5.0 the client each connection uses never times out before `SendTimeoutSeconds` does, so a small `Timeout` cannot fail the queue's sends.

The worker also manages the SMTP connection itself instead of sharing one `SmtpClient` for the life of the process. A connection is reused only between sends less than 30 seconds apart and for at most 50 messages, and it is closed with `QUIT` when the pass ends. Before, an asynchronous send never closed its connection, and the next send, however much later, wrote `MAIL FROM` onto it. A connection that a NAT dropped while idle (Azure drops idle flows silently after 4 minutes) never answered, and the send waited about 16 minutes for the operating system to give up on TCP, holding every email queued behind it.

The `SmtpClient` you configure (`options.SmtpClient`, or the `Smtp` section) is used as a template: host, port, SSL, credentials, delivery method and certificates are copied to the client each connection uses. The message sent is the one FluentEmail.Smtp would have built. If you register an `ISender` other than FluentEmail's `SmtpSender`, the worker still calls it and stops waiting after `SendTimeoutSeconds`; a send your sender does not cancel keeps running in the background and may still go out, after which the retry sends it again.

**Sending several emails at once (2.4.0).** <a id="sending-several-emails-at-once-240"></a>With `MaxConcurrentSends` = N above 1, each round of a pass reads up to max(10, 2N) due rows of the top priority (one per partition, in turn order, when several partitions have mail) and hands them to N senders. Each sender has its own database context and its own SMTP connection (or calls your `ISender`), and claims, sends and records its row exactly as the single sender does: CAS claim, claim deadline, send timeout, retry budget, `LastError`. The next round starts when the round's sends have ended, so a higher-priority email queued meanwhile waits at most one round, which with N senders is shorter than before. What stays sequential:
- The probe after an outage pause is always one email, alone.
- Once an outage pause is declared, or the host is stopping, or a row fails with an exception, no sender starts a new email; the sends already under way finish (each bounded by `SendTimeoutSeconds`) and are recorded. A failure that arrives while the pause runs is recorded on its row but does not lengthen the pause.
- One pass at a time per process, as before: N is the most connections one worker opens to the mail server.

Choose N from the mail server's limits, not from the queue length: N connections can be open at once, and the server may refuse more (`421 Too many connections`). Your `ISender`, if you register one, must be safe to call from N threads at once (the built-in SMTP path uses one connection per sender). The database sees the same number of commands per email; up to N + 1 pooled connections are used during a drain.

**Database cost.** The worker never polls faster than `PollIntervalSeconds` (floor 30 seconds) and never retries in a loop. An idle wake is 2 queries. While draining it is 2 queries per email plus 2 per batch of up to 10 emails (one batch per turn between partitions when several have mail). The stale-claim check adds no query unless a stale row exists.

### Configuration keys
All of them are optional and bind from the same section as the SMTP settings.

| Key | Default | Meaning |
|---|---|---|
| `MaxRetryAttempts` | 3 | Waits before giving up (N waits, N + 1 sends) |
| `RetryBackoffSeconds` | 30 | First wait; each further wait doubles |
| `MaxRetryBackoffSeconds` | 0 (no ceiling) | 2.3.0. Ceiling on one wait, in seconds |
| `OutagePauseThreshold` | 0 (off) | 2.3.0. Different emails failing on transport, with no success in between, that pause the drain |
| `StaleClaimMinutes` | 10 | Minutes in `Sending` before a claim is considered orphaned. Bindable from configuration since 2.3.0 (before, only in code) |
| `PartitionFairness` | true | 2.3.0. Take turns between partition keys within a priority |
| `PollIntervalSeconds` | 120 | 2.3.0. Seconds between periodic wakes; 1 to 29 is raised to 30; 0 turns the periodic wake off |
| `SendTimeoutSeconds` | 60 | 2.3.0. Bound on one send, connect included; floor 5, at most 80% of `StaleClaimMinutes`. A send past it fails and waits its retry backoff |
| `MaxConcurrentSends` | 1 | 2.4.0. Emails one worker sends at the same time, each on its own connection; 0 or less means 1, capped at 16. 1 is the 2.3.0 behaviour |

`RunHostedService` (default true) and `FinishedModelScrubber` (default null, 2.5.0) are set in code only.

You can manually inspect or reset failed tasks (e.g., set `Status = Pending`, `RetryCount = 0`, `NextAttemptOn = null`) if you introduce a manual admin UI (not included by default).

### Upgrading from pre-2.1.0
1. Update your target framework to `net8.0` (if not already).
2. Apply EF Core migrations if you persist tasks (add new nullable columns `RetryCount` (int, default 0) and `NextAttemptOn` (datetime null) & `SentOn`). If using InMemory provider (example project) no action needed.
3. (Optional) Add retry settings in configuration.
4. Rebuild; no code changes required for basic usage.

### Upgrading to 2.3.0
**1. Add four columns to the queue table.** The library ships no migration and does not create or alter tables: the host owns the schema. The columns must exist before 2.3.0 code runs, because both `EnqueueAsync` and the worker name them.

| Column | Type (SQL Server) | Nullable | Default |
|---|---|---|---|
| `Priority` | `int` | NOT NULL | `0` |
| `PartitionKey` | `nvarchar(64)` | NULL | none |
| `LastError` | `nvarchar(1000)` | NULL | none |
| `LastErrorOn` | `datetime2` | NULL | none |

If your `DbContext` calls `(this as IEmailQueuerContext).Initialize(modelBuilder)`, the default and the two lengths are already part of the model, and `dotnet ef migrations add <Name>` should generate the equivalent of:
```csharp
migrationBuilder.AddColumn<int>(name: "Priority", table: "EmailQueuerTasks", type: "int", nullable: false, defaultValue: 0);
migrationBuilder.AddColumn<string>(name: "PartitionKey", table: "EmailQueuerTasks", type: "nvarchar(64)", maxLength: 64, nullable: true);
migrationBuilder.AddColumn<string>(name: "LastError", table: "EmailQueuerTasks", type: "nvarchar(1000)", maxLength: 1000, nullable: true);
migrationBuilder.AddColumn<DateTime>(name: "LastErrorOn", table: "EmailQueuerTasks", type: "datetime2", nullable: true);
```
If it does not call `Initialize`, the two strings are generated as `nvarchar(max)`; the library enforces the lengths in code either way. The change only adds columns (three nullable, one with a default), so a process still running 2.2.x keeps working against the new schema: deploy the migration together with, or before, the package upgrade, and a rollback of the package needs no rollback of the schema. No index is added.

**2. Nothing to change in code.** The existing `EnqueueAsync` overloads are unchanged; existing rows and rows queued by them are normal priority (0) with no partition key.

**3. What behaves differently with no configuration change.**
- The worker now wakes every 120 seconds (`PollIntervalSeconds`), 2 small queries when the queue is idle. Set it to `0` to keep the previous behaviour, for instance on a database that pauses itself when idle.
- A stale claim increments `RetryCount`, and can therefore end `PermanentlyFailed`.
- A send still running after `SendTimeoutSeconds` (60 by default) is aborted and recorded as a failed attempt. Before, a send that never returned stopped the worker until the connection died (about 16 minutes on Linux), or for the life of the process. (The 2.3.0-rc.1 pre-release tried to cancel after 80% of `StaleClaimMinutes`, but the cancellation never reached the SMTP client.)
- The SMTP connection is no longer kept open between passes: one connection per pass, reused only while sends are less than 30 seconds apart, closed with `QUIT` at the end of the pass.
- A send whose `SendResponse.Successful` is false is now a failed attempt (it was recorded as sent). The built-in SMTP sender reports failures by throwing, so this only matters with a custom `ISender`.
- The log lines carry the task id instead of the recipient address.
- One retry wait is never longer than 24 hours.

**4. Optional.** Set `MaxRetryBackoffSeconds`, `OutagePauseThreshold` and a larger `MaxRetryAttempts` to ride out a mail server outage (see the budget table above). Do not raise `MaxRetryAttempts` past about 20 on 2.2.1 or older: the wait overflows there.

### Upgrading to 2.5.0
**1. Nothing to change in code or in the schema.** The public API only gains `SendDirectAsync` and the `FinishedModelScrubber` option (null by default, which keeps every model as before); the queue table, the statuses, the claim, the retries and the outage pause are those of 2.4.0. The background pass that an `EnqueueAsync` starts still runs in that caller's async context (culture, request state, `AsyncLocal` values), as before.

One behaviour change, for `SendDirectAsync` only: an attachment whose `ContentId` contains a control character (a line break for instance) is refused with `ArgumentException`, because System.Net.Mail writes the Content-ID into the part's headers as is. Queued emails attach files by path, which sets no Content-ID, so the queue is not affected.

**2. Pick up the target that matches your app.** A net8.0 app gets the net8.0 build (EF Core 8), a net10.0 app the net10.0 build (EF Core 10). NuGet gives other targets the nearest lower build: net9.0 gets the net8.0 build, net11.0 the net10.0 build (not tested with EF Core 11). The 2.4.0 build cannot run on EF Core 10: its first `ExecuteUpdateAsync` (the claim) fails, because EF Core 10 replaced the `SetPropertyCalls` setter type with `UpdateSettersBuilder`.
- A net10.0 app must use EF Core 10 with 2.5.0. An app that has to stay on EF Core 8 or 9 while targeting net10.0 keeps 2.4.0.
- Microsoft support for .NET 8 ended on 10 November 2026. The net8.0 build is there so .NET 8 apps can take 2.5.0 and move to .NET 10 when they are ready.

**3. Dependency floors.** NuGet takes the higher of your version and these. A direct reference to a lower version fails the restore with `NU1605` (package downgrade): raise it, or remove it and let the package bring it.

| Target | EF Core and EF Core Relational | Microsoft.Extensions.Hosting.Abstractions | PreMailer.Net | Razor.Templating.Core |
|---|---|---|---|---|
| net8.0 | 8.0.10 | 8.0.0 | 2.7.4 | 2.0.0 |
| net10.0 | 10.0.0 | 10.0.0 | 2.7.4 | 2.0.0 |

EF Core 8.0.10 is the first EF Core 8 patch whose `Microsoft.Extensions.Caching.Memory` dependency is not affected by CVE-2024-43483. PreMailer.Net 2.7.4 replaces AngleSharp 0.14.0 (GHSA-pgww-w46g-26qg) with 1.7.2. A pin of PreMailer.Net below 2.7.4 must be raised: the `MoveCssInline` overload 2.5.0 calls is missing from older PreMailer versions (2.2.0 for one), which would fail every email with `MissingMethodException`.

AngleSharp moves from 0.x to 1.x. A package that pins AngleSharp 0.x exactly fails the restore with `NU1107` (version conflict); HtmlSanitizer 9 and older pin 0.17.1, HtmlSanitizer 10 and later use AngleSharp 1.x and work. The CSS moved inline is byte for byte the same as with 2.4.0 on the library's test templates (layout, nested styles, tables, comments, entities); check a few of your own emails after upgrading.

Razor.Templating.Core 2.0.0 removed `RazorTemplateEngine.Initialize()` and the generic `RenderAsync<TModel>`. Code of yours that calls them through this package's reference moves to `RazorTemplateEngine.RenderAsync(viewName, model, viewData)`.

**4. The ASP.NET Core runtime is still required.** Razor.Templating.Core references the `Microsoft.AspNetCore.App` shared framework, as in every earlier version, so a generic-host (worker) app needs the ASP.NET Core runtime too.

**5. Keep Razor.Templating.Core at 2.0.0.** 2.1.0 renders with the ambient MVC `ActionContext`. The background pass inherits the context of the caller that started it (point 1), so with 2.1.0 a queued email can render with the context of the request that queued it, and fail once that request has ended. The library's test for this fails with 2.1.0 and passes with 2.0.0.

**6. `SmtpClient.Timeout` on .NET 10.** The value is in milliseconds, and .NET 10 applies it to asynchronous sends, which .NET 8 does not (dotnet/runtime PR 115722 made `SendMailAsync` share the synchronous path and its timer). The queue's own connections never time out before `SendTimeoutSeconds`, so nothing changes for the queue; any other code of yours that sends through an `SmtpClient` with a small `Timeout` will now time out.

**7. Addresses with two consecutive dots on .NET 10.** The .NET 10 runtime refuses an address such as `a..b@example.com` (`FormatException`), which .NET 8 accepted. This follows the runtime, not the build: a net8.0 app running on .NET 10 is affected too. Such an email fails every attempt and ends `PermanentlyFailed`; check addresses before queueing them.

### Notes
- The library does not create tables automatically; manage schema via migrations.
- Razor template assembly root class must still be supplied to `AddEmailQueuer`.
- Avoid storing secrets (SMTP credentials) in source control; use user-secrets or environment variables.
