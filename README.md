# Email Queuer
A library for ASP.NET Core (.NET 8) that allows you to send emails without memory leaks and in a very clean way.

Features:
- Background email queue persisted to your database (no in-memory growth / leaks)
- Automatic retry with exponential backoff (configurable) and permanent failure cut-off
- Saved status & metadata so you can inspect / manually resend failed emails
- Razor templating engine support: RazorLight (version < 1.0.5.3) or Razor.Templating.Core (>= 2.0.0)
- Global CSS moved inline for email client compatibility (optional)
- Configurable template path & dynamic ViewBag

New in >= 2.1.0:
- Target framework upgraded to net8.0
- Retry logic with backoff (RetryCount, NextAttemptOn, MaxRetryAttempts, RetryBackoffSeconds)
- New status: `PermanentlyFailed` once retries are exhausted
- Additional task fields: `RetryCount`, `NextAttemptOn`, `SentOn`

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
  "RetryBackoffSeconds": 30
  }
```

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

### Retry & Failure Handling
Each task moves through these statuses:
- `Pending` -> queued and not yet processed
- `Sending` -> actively being sent
- `Sent` -> delivered successfully
- `Error` -> last attempt failed; will be retried if `RetryCount < MaxRetryAttempts`
- `PermanentlyFailed` -> all retry attempts exhausted (no further automatic retries)

Exponential backoff formula: `nextDelay = RetryBackoffSeconds * 2^(RetryCount-1)` (capped only by your own configuration / observation). The scheduler sleeps between batches and wakes only when the next due attempt time arrives, minimizing CPU/DB polling.

You can manually inspect or reset failed tasks (e.g., set `Status = Pending`, `RetryCount = 0`, `NextAttemptOn = null`) if you introduce a manual admin UI (not included by default).

### Upgrading from pre-2.1.0
1. Update your target framework to `net8.0` (if not already).
2. Apply EF Core migrations if you persist tasks (add new nullable columns `RetryCount` (int, default 0) and `NextAttemptOn` (datetime null) & `SentOn`). If using InMemory provider (example project) no action needed.
3. (Optional) Add retry settings in configuration.
4. Rebuild; no code changes required for basic usage.

### Notes
- The library does not create tables automatically; manage schema via migrations.
- Razor template assembly root class must still be supplied to `AddEmailQueuer`.
- Avoid storing secrets (SMTP credentials) in source control; use user-secrets or environment variables.
