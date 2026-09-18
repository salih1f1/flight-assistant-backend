using FlightAssistant.Domain.DataSources.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.DataSources
{
    /// <summary>
    /// The <c>data_source_status</c> table: one row per catalog key, upserted by the buffered
    /// observer. snake_case columns, matching <c>verification_requests</c>.
    /// </summary>
    public sealed class DataSourceStatusConfiguration : IEntityTypeConfiguration<DataSourceStatus>
    {
        public void Configure(EntityTypeBuilder<DataSourceStatus> b)
        {
            b.ToTable("data_source_status");

            // The catalog key is the natural key. No surrogate id: there is exactly one row per
            // source and the key is what every read and write addresses it by.
            b.HasKey(x => x.Key);

            b.Property(x => x.Key).HasColumnName("key").HasMaxLength(64);
            b.Property(x => x.LastOutcome).HasColumnName("last_outcome").HasMaxLength(32);
            b.Property(x => x.LastOutcomeUtc).HasColumnName("last_outcome_utc");
            b.Property(x => x.LastOkUtc).HasColumnName("last_ok_utc");
            b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(1024);
            b.Property(x => x.ConsecutiveFailures).HasColumnName("consecutive_failures");
            b.Property(x => x.LastProbeUtc).HasColumnName("last_probe_utc");
            b.Property(x => x.LastProbeBy).HasColumnName("last_probe_by").HasMaxLength(256);

            // No index beyond the primary key: the table has one row per source — nine today — so
            // every read is a full scan of a handful of rows or a PK seek.
        }
    }
}
