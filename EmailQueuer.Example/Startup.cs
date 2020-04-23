using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using EmailQueuer.Extensions;
using Microsoft.Extensions.Configuration;
using EmailQueuer.Example.Mails;

namespace EmailQueuer.Example
{
    public class Startup
    {
        private readonly IConfiguration configuration;

        public Startup(IConfiguration configuration)
        {
            this.configuration = configuration;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase(nameof(AppDbContext));
            });

            services.AddControllers();

            // Adding the email queuer
            services.AddEmailQueuer<AppDbContext>(typeof(Templates), options =>
            {
                // Loading from appsettings.json
                options.LoadFromConfiguration(configuration);

                // You can also manually configure them
                /* options.Sender.UserName = "sender@example.com";
                options.Sender.Password = "123456";
                options.SmtpClient.Host = "smtp.gmail.com";
                options.SmtpClient.Port = 587;
                options.SmtpClient.EnableSsl = true;
                options.SmtpClient.Timeout = 20;
                options.ViewBag.WebsiteLink = "https://github.com/omneimneh/email-queuer";
                options.MoveCssInline = true; */
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapDefaultControllerRoute();
            });
        }
    }
}
