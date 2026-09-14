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

            b.HasIndex(x => x.Iata).IsUnique();
            b.HasIndex(x => x.Icao).IsUnique().HasFilter("\"Icao\" IS NOT NULL");
            b.HasIndex(x => new { x.CountryCode, x.City });

            b.Property(x => x.IsVerified).HasColumnName("is_verified").HasDefaultValue(false);
            b.Property(x => x.VerifiedAt).HasColumnName("verified_at");
            b.Property(x => x.VerifiedBy).HasColumnName("verified_by").HasMaxLength(255);
            b.Property(x => x.VerificationSource).HasColumnName("verification_source").HasMaxLength(100);
            b.Property(x => x.VerificationIssue).HasColumnName("verification_issue").HasMaxLength(500);

            b.HasOne<Country>()
                .WithMany()
                .HasForeignKey(x => x.CountryCode)
                .HasPrincipalKey("Code")
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
