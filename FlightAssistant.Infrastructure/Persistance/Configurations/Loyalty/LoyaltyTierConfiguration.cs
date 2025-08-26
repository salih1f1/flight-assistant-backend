using FlightAssistant.Domain.Loyalty.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Loyalty
{
    public sealed class LoyaltyTierConfiguration : IEntityTypeConfiguration<LoyaltyTier>
    {
        public void Configure(EntityTypeBuilder<LoyaltyTier> b)
        {
            b.ToTable("loyalty_tiers");
            b.HasKey(x => x.Id);

            b.Property(x => x.Code).IsRequired().HasMaxLength(24);
            b.Property(x => x.Name).IsRequired().HasMaxLength(200);
            b.Property(x => x.Rank).IsRequired();
            b.Property(x => x.AllianceCode).HasMaxLength(8);
            b.Property(x => x.AllianceTierCode).HasMaxLength(16);

            b.HasOne<LoyaltyProgram>()
                .WithMany(p => p.Tiers)
                .HasForeignKey(x => x.ProgramId)
                .OnDelete(DeleteBehavior.Cascade);

            // A program can’t have two tiers with same code or rank
            b.HasIndex(x => new { x.ProgramId, x.Code }).IsUnique();
            b.HasIndex(x => new { x.ProgramId, x.Rank }).IsUnique();
        }
    }
}
