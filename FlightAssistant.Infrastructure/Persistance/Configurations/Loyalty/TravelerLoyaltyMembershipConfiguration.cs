using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Loyalty
{
    public sealed class TravelerLoyaltyMembershipConfiguration : IEntityTypeConfiguration<TravelerLoyaltyMembership>
    {
        public void Configure(EntityTypeBuilder<TravelerLoyaltyMembership> b)
        {
            b.ToTable("traveler_loyalty_memberships");
            b.HasKey(x => x.Id);

            b.Property(x => x.AccountNumber).IsRequired().HasMaxLength(64);

            b.HasIndex(x => new { x.TravelerId, x.ProgramId }).IsUnique(); // one membership per program per traveler

            b.HasOne<Traveler>().WithMany()
                .HasForeignKey(x => x.TravelerId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne<LoyaltyProgram>().WithMany()
                .HasForeignKey(x => x.ProgramId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne<LoyaltyTier>().WithMany()
                .HasForeignKey(x => x.TierId)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }
}
