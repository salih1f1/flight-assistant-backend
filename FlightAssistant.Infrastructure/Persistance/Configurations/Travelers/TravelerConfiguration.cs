using FlightAssistant.Domain.Travelers.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Travelers
{
    public class TravelerConfiguration : IEntityTypeConfiguration<Traveler>
    {
        public void Configure(EntityTypeBuilder<Traveler> b)
        {
            b.ToTable("travelers");
            b.HasKey(x => x.Id);

            b.Property(x => x.Email).IsRequired().HasMaxLength(254);
            b.HasIndex(x => x.Email).IsUnique();

            b.Property(x => x.DisplayName).HasMaxLength(100);

            b.Property(x => x.CreatedUtc).HasColumnType("timestamptz");
            b.Property(x => x.UpdatedUtc).HasColumnType("timestamptz");

            b.HasMany(x => x.DeviceTokens)
             .WithOne()
             .HasForeignKey(dt => dt.TravelerId)
             .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
