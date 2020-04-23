using CodeSwitch.Utils.EmailQueuer.Models;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitch.Utils.EmailQueuer.Example
{
    public class AppDbContext : DbContext, IEmailQueuerContext
    {
        public virtual DbSet<EmailQueuerTask> EmailQueuerTasks { get; set; }

        public AppDbContext(DbContextOptions options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            (this as IEmailQueuerContext).Initialize(modelBuilder);
            base.OnModelCreating(modelBuilder);
        }
    }
}
