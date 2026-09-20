using FlightAssistant.Domain.Travelers.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Travelers
{
    public class DeviceTokenConfiguration : IEntityTypeConfiguration<DeviceToken>
    {
        public void Configure(EntityTypeBuilder<DeviceToken> b)
        {
            b.ToTable("device_tokens");
            b.HasKey(x => x.Id);

            // The application generates this key, never the store: BaseEntity assigns
            // Guid.NewGuid() in its field initializer. Saying so is not cosmetic (#546). EF's own
            // convention would mark a Guid key ValueGeneratedOnAdd, and its graph attacher reads a
            // value-generated key that already carries a value as an existing row -- so a token
            // added to a traveler loaded with .Include(t => t.DeviceTokens) was tracked Modified
            // rather than Added, and SaveChangesAsync issued an UPDATE matching no row at all:
            // DbUpdateConcurrencyException, a 500 on every first registration of a token.
            // DeviceToken is the only entity in the domain with this shape -- the other two
            // collection navigations that gain children, LoyaltyProgram.Tiers and
            // Lounge.Providers, are keyed by an int left at its default 0, which EF tracks Added
            // correctly. Annotation only; it changes no column.
            b.Property(x => x.Id).ValueGeneratedNever();

            b.Property(x => x.TravelerId).IsRequired();

            b.Property(x => x.Platform).IsRequired().HasMaxLength(16);
            b.Property(x => x.Token).IsRequired().HasMaxLength(2048);
            b.Property(x => x.LastSeenUtc).HasColumnType("timestamptz");

            b.HasIndex(x => new { x.TravelerId, x.Token }).IsUnique();
        }
    }
}
