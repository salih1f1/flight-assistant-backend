using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.ReferenceData
{
    public sealed class AirlineConfiguration : IEntityTypeConfiguration<Airline>
    {
        public void Configure(EntityTypeBuilder<Airline> b)
        {
            b.ToTable("airlines");
            b.HasKey(x => x.Id);

            b.Property(x => x.Iata).IsRequired().HasMaxLength(3);
            b.Property(x => x.Icao).HasMaxLength(4);
            b.Property(x => x.Name).IsRequired().HasMaxLength(200);
            b.Property(x => x.CountryCode).IsRequired().HasMaxLength(2).IsFixedLength();
            b.Property(x => x.AllianceCode).HasMaxLength(8);

            b.HasIndex(x => x.Iata).IsUnique();
            b.HasIndex(x => x.Icao).IsUnique().HasFilter("\"Icao\" IS NOT NULL");

            b.HasOne(x => x.Alliance)
                .WithMany(a => a.Airlines)
                .HasForeignKey(x => x.AllianceCode)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasOne<Country>()
                .WithMany()
                .HasForeignKey(x => x.CountryCode)
                .HasPrincipalKey("Code")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
