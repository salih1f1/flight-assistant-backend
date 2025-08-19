using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.ReferenceData
{
    public sealed class AirportConfiguration : IEntityTypeConfiguration<Airport>
    {
        public void Configure(EntityTypeBuilder<Airport> b)
        {
            b.ToTable("airports");
            b.HasKey(x => x.Id);

            b.Property(x => x.Iata).IsRequired().HasMaxLength(3);
            b.Property(x => x.Icao).HasMaxLength(4);
            b.Property(x => x.Name).IsRequired().HasMaxLength(200);
            b.Property(x => x.City).IsRequired().HasMaxLength(200);
            b.Property(x => x.CountryCode).IsRequired().HasMaxLength(2).IsFixedLength();
            b.Property(x => x.Timezone).HasMaxLength(64);

            b.HasIndex(x => x.Iata).IsUnique();
            b.HasIndex(x => x.Icao).IsUnique().HasFilter("\"Icao\" IS NOT NULL");
            b.HasIndex(x => new { x.CountryCode, x.City });

            b.HasOne<Country>()
                .WithMany()
                .HasForeignKey(x => x.CountryCode)
                .HasPrincipalKey("Code")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
