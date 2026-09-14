using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Loyalty
{
    public sealed class LoyaltyProgramConfiguration : IEntityTypeConfiguration<LoyaltyProgram>
    {
        public void Configure(EntityTypeBuilder<LoyaltyProgram> b)
        {
            b.ToTable("loyalty_programs");
            b.HasKey(x => x.Id);

            b.Property(x => x.Code).IsRequired().HasMaxLength(16);
            b.Property(x => x.Name).IsRequired().HasMaxLength(200);
            b.Property(x => x.AllianceCode).HasMaxLength(8);

            b.HasIndex(x => x.Code).IsUnique();

            b.HasOne<LoyaltyProgramKind>(x => x.Kind)
                .WithMany(k => k.Programs)
                .HasForeignKey(x => x.KindId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne<Airline>()
                .WithMany()
                .HasForeignKey(x => x.AirlineId)
                .OnDelete(DeleteBehavior.SetNull);

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
