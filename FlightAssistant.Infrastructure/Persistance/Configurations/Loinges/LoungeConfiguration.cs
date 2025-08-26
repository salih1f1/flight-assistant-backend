using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Loinges
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
        }
    }
}
