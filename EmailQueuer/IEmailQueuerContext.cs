using EmailQueuer.Models;
using Microsoft.EntityFrameworkCore;

namespace EmailQueuer
{
    public interface IEmailQueuerContext
    {
        public DbSet<EmailQueuerTask> EmailQueuerTasks { get; set; }
        public void Initialize(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EmailQueuerTask>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
        }
    }
}
