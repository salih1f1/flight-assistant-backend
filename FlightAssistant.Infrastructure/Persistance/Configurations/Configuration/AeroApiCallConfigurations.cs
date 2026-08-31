using FlightAssistant.Domain.Configuration.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Configuration
{
    public sealed class AeroApiCallConfigurations : IEntityTypeConfiguration<AeroApiCall>
    {
        public void Configure(EntityTypeBuilder<AeroApiCall> b)
        {
            b.ToTable("aeroapi_calls");
            b.HasKey(x => x.Id);

            b.Property(x => x.EndpointTemplate).IsRequired().HasMaxLength(128);
            b.Property(x => x.Method).IsRequired().HasMaxLength(8);
            b.Property(x => x.Caller).IsRequired().HasMaxLength(32);
            b.Property(x => x.CalledUtc).HasColumnType("timestamptz");

            // Stored as a string so a migration reordering the enum can never silently rewrite history.
            b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);

            // Every query is a CalledUtc range, and most also group by template. One composite
            // covers both: Postgres serves a leading-column range scan from it, so a separate
            // index on CalledUtc alone would only be write amplification on a table that takes an
            // insert per outbound call and a bulk delete every day.
            b.HasIndex(x => new { x.CalledUtc, x.EndpointTemplate });
        }
    }
}
