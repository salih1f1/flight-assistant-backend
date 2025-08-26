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

            b.Property(x => x.TravelerId).IsRequired();

            b.Property(x => x.Platform).IsRequired().HasMaxLength(16);
            b.Property(x => x.Token).IsRequired().HasMaxLength(2048);
            b.Property(x => x.LastSeenUtc).HasColumnType("timestamptz");

            b.HasIndex(x => new { x.TravelerId, x.Token }).IsUnique();
        }
    }
}
