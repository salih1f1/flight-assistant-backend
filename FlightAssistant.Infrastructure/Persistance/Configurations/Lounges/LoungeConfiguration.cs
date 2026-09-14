using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Lounges
{
    public sealed class LoungeConfiguration : IEntityTypeConfiguration<Lounge>
    {
        public void Configure(EntityTypeBuilder<Lounge> b)
        {
            b.ToTable("lounges");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired().HasMaxLength(200);
            b.Property(x => x.Terminal).HasMaxLength(32);
            b.Property(x => x.LocationHint).HasMaxLength(200);
            b.Property(x => x.Hours).HasMaxLength(200);
            b.Property(x => x.Amenities).HasMaxLength(1000);

            b.HasOne<Airport>().WithMany().HasForeignKey(x => x.AirportId)
              .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.AirportId, x.Terminal, x.Name }).IsUnique();

            // Nullable with no default: existing rows stay unknown rather than all looking new (#334).
            b.Property(x => x.CreatedUtc).HasColumnName("created_utc");

            b.Property(x => x.IsVerified).HasColumnName("is_verified").HasDefaultValue(false);
            b.Property(x => x.VerifiedAt).HasColumnName("verified_at");
            b.Property(x => x.VerifiedBy).HasColumnName("verified_by").HasMaxLength(255);
            b.Property(x => x.VerificationSource).HasColumnName("verification_source").HasMaxLength(100);
            b.Property(x => x.VerificationIssue).HasColumnName("verification_issue").HasMaxLength(500);
        }
    }
}
