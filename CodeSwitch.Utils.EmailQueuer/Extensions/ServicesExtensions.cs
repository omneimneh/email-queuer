using CodeSwitch.Utils.EmailQueuer.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace CodeSwitch.Utils.EmailQueuer.Extensions
{
    public static class ServicesExtensions
    {
        /// <summary>
        /// Add email queuer with all dependencies to your DI container
        /// </summary>
        /// <typeparam name="TContext">Database context where your email queue is stored</typeparam>
        /// <param name="services">DI container</param>
        /// <param name="assembly">Class type at the root of your email templates folder</param>
        /// <param name="configure">Configure method for the email queuer configuration</param>
        public static void AddEmailQueuer<TContext>(this IServiceCollection services, Type assembly, Action<EmailQueuerOptions> configure)
            where TContext : DbContext, IEmailQueuerContext
        {
            var options = EmailQueuerOptions.Default;
            configure(options);

            options.Assembly = assembly;

            services.AddFluentEmail(options.Sender.UserName)
                .AddSmtpSender(options.SmtpClient);

            services.AddSingleton(options);
            services.AddSingleton<EmailQueuer<TContext>>();
            services.AddHostedService(x => x.GetRequiredService<EmailQueuer<TContext>>());
        }
    }
}
