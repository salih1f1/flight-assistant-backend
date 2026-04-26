using FlightAssistant.Domain.Lounges.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Lounges
{
    public sealed class LoungeProviderLinkConfiguration : IEntityTypeConfiguration<LoungeProviderLink>
    {
        public void Configure(EntityTypeBuilder<LoungeProviderLink> b)
        {
            b.ToTable("lounge_provider_links");
            b.HasKey(x => new { x.LoungeId, x.ProviderId, x.Role });

            b.Property(x => x.Role).HasConversion<int>();
            b.Property(x => x.Notes).HasMaxLength(4000);

            b.HasOne(x => x.Lounge).WithMany(l => l.Providers).HasForeignKey(x => x.LoungeId)
              .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.Provider).WithMany(p => p.LoungeLinks).HasForeignKey(x => x.ProviderId)
              .OnDelete(DeleteBehavior.Cascade);

            b.HasIndex(x => new { x.ProviderId, x.Role });
        }
    }
}
