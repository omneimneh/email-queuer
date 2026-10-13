using CodeSwitch.Utils.EmailQueuer.Models;
using Microsoft.EntityFrameworkCore;

namespace CodeSwitch.Utils.EmailQueuer
{
    public interface IEmailQueuerContext
    {
        public DbSet<EmailQueuerTask> EmailQueuerTasks { get; set; }
        public void Initialize(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EmailQueuerTask>(entity =>
            {
                entity.HasKey(e => e.Id);
                // 2.3.0 columns. The library ships no migration: the host generates its own
                // (see README, "Upgrading to 2.3.0").
                entity.Property(e => e.Priority).HasDefaultValue(0);
                entity.Property(e => e.PartitionKey).HasMaxLength(EmailQueuerTask.PartitionKeyMaxLength);
                entity.Property(e => e.LastError).HasMaxLength(EmailQueuerTask.LastErrorMaxLength);
            });
        }
    }
}
