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

            // Normalize storage of codes to uppercase (domain does not currently enforce)
            b.Property(x => x.Iata)
                .HasMaxLength(3)
                .HasConversion(
                    v => v.ToUpperInvariant(),
                    v => v); // no down-conversion needed
            b.Property(x => x.Icao)
                .HasMaxLength(4)
                .HasConversion(
                    v => v == null ? null : v.ToUpperInvariant(),
                    v => v);

            b.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(200);

            b.Property(x => x.CountryCode)
                .IsRequired()
                .HasMaxLength(2)
                .IsFixedLength()
                .HasConversion(
                    v => v.ToUpperInvariant(),
                    v => v);

            b.Property(x => x.AllianceCode)
                .HasMaxLength(8)
                .HasConversion(
                    v => v == null ? null : v.ToUpperInvariant(),
                    v => v);

            b.Property(x => x.IsVerified).HasColumnName("is_verified").HasDefaultValue(false);
            b.Property(x => x.VerifiedAt).HasColumnName("verified_at");
            b.Property(x => x.VerifiedBy).HasColumnName("verified_by").HasMaxLength(255);

            // Indexes (case-insensitive uniqueness by normalizing value via converters)
            b.HasIndex(x => x.Iata).IsUnique();
            b.HasIndex(x => x.Icao).IsUnique().HasFilter("\"Icao\" IS NOT NULL");
            b.HasIndex(x => x.CountryCode);
            b.HasIndex(x => x.AllianceCode).HasDatabaseName("ix_airlines_alliance");

            // Relationships
            b.HasOne(x => x.Alliance)
                .WithMany(a => a.Airlines)
                .HasForeignKey(x => x.AllianceCode)
                .HasPrincipalKey(a => a.Code)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasOne<Country>()
                .WithMany()
                .HasForeignKey(x => x.CountryCode)
                .HasPrincipalKey("Code")
                .OnDelete(DeleteBehavior.Restrict);

            // Flights collection (inverse already configured in FlightConfiguration; harmless to define here)
            b.HasMany(x => x.Flights)
                .WithOne(f => f.Airline)
                .HasForeignKey(f => f.AirlineId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
