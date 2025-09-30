using FlightAssistant.Domain.Configuration.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Configuration
{
    public sealed class SeedVersionConfigurations : IEntityTypeConfiguration<SeedVersion>
    {
        public void Configure(EntityTypeBuilder<SeedVersion> builder)
        {
            builder.ToTable("seed_versions");
            builder.HasKey(x => new { x.Dataset, x.Version });
            builder.Property(x => x.AppliedAt)
                   .HasDefaultValueSql("NOW()");
        }
    }
}
