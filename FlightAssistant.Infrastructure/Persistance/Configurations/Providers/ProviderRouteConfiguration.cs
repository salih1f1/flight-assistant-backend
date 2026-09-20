using FlightAssistant.Domain.Providers.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Providers
{
    /// <summary>The <c>provider_routes</c> table: one row per routing key. snake_case columns,
    /// matching <c>job_switches</c> beside it.</summary>
    public sealed class ProviderRouteConfiguration : IEntityTypeConfiguration<ProviderRoute>
    {
        public void Configure(EntityTypeBuilder<ProviderRoute> b)
        {
            b.ToTable("provider_routes");

            // The key name is the natural key: exactly one row per decision, every read and write
            // addresses it by name.
            b.HasKey(x => x.Job);

            b.Property(x => x.Job).HasColumnName("job").HasMaxLength(64);
            b.Property(x => x.Providers).HasColumnName("providers").HasMaxLength(256);
            b.Property(x => x.ChangedUtc).HasColumnName("changed_utc");
            b.Property(x => x.ChangedBy).HasColumnName("changed_by").HasMaxLength(256);
            b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(512);
        }
    }
}
