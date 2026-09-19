using FlightAssistant.Domain.Jobs.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Jobs
{
    /// <summary>
    /// The <c>job_switches</c> table: one row per job, keyed by the job name. snake_case columns,
    /// matching <c>verification_requests</c> and <c>data_source_status</c>.
    /// </summary>
    public sealed class JobSwitchConfiguration : IEntityTypeConfiguration<JobSwitch>
    {
        public void Configure(EntityTypeBuilder<JobSwitch> b)
        {
            b.ToTable("job_switches");

            // The job name is the natural key: there is exactly one row per job and every read
            // and write addresses it by name.
            b.HasKey(x => x.Job);

            b.Property(x => x.Job).HasColumnName("job").HasMaxLength(64);
            b.Property(x => x.Enabled).HasColumnName("enabled");
            b.Property(x => x.ChangedUtc).HasColumnName("changed_utc");
            b.Property(x => x.ChangedBy).HasColumnName("changed_by").HasMaxLength(256);
            b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(512);
        }
    }
}
