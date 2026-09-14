using FlightAssistant.Domain.Lounges.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Lounges
{
    public sealed class LoungeProviderRefConfiguration : IEntityTypeConfiguration<LoungeProvider>
    {
        public void Configure(EntityTypeBuilder<LoungeProvider> b)
        {
            b.ToTable("lounge_providers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).IsRequired().HasMaxLength(32);
            b.Property(x => x.Name).IsRequired().HasMaxLength(100);
            b.Property(x => x.Kind).HasConversion<int>();
            b.Property(x => x.Description).HasMaxLength(4000);
            b.HasIndex(x => x.Code).IsUnique();

            b.Property(x => x.IsVerified).HasColumnName("is_verified").HasDefaultValue(false);
            b.Property(x => x.VerifiedAt).HasColumnName("verified_at");
            b.Property(x => x.VerifiedBy).HasColumnName("verified_by").HasMaxLength(255);
            b.Property(x => x.VerificationSource).HasColumnName("verification_source").HasMaxLength(100);
            b.Property(x => x.VerificationIssue).HasColumnName("verification_issue").HasMaxLength(500);
        }
    }
}
