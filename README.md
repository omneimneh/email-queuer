# Email Queuer
A library for asp.net core that allows you to send emails without having memory leaks and in a very clean way.
This library can do the following:
- Send emails in the background without risking a Memory crash
- Saves the status of each email so you can resend emails that encountered errors while sending
- Use razor templating engine (RazorLight)
- Move global css inline to support email styling without the headache of inline styles.

## How to use
Follow these steps to get your email queuer up and running

#### Install package from Nuget
Run the following command in your package manager console: `Install-Package EmailQueuer`

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
The email queuer main objective is to avoid memory leaks, so the email queue should be stored in the database, so your `DbContext` should implement `IEmailQueuerContext` like the following:

```csharp
public class AppDbContext : DbContext, IEmailQueuerContext
{
    public AppDbContext(DbContextOptions options) : base(options) { }
   
    // Add the rqeuired table
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

This is how your `appsettings.json` should look like, replace `<SectionName>` with the one you used above:
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
    "MoveCssInline": true
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
