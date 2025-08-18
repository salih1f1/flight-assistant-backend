using FlightAssistant.Domain.ReferenceData.Entities;
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
            b.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength();

            b.Property(x => x.CreatedUtc).HasColumnType("timestamptz");
            b.Property(x => x.UpdatedUtc).HasColumnType("timestamptz");

            b.Property(x => x.NationalityCountryCode).HasMaxLength(2).IsFixedLength();
            b.Property(x => x.ResidenceCountryCode).HasMaxLength(2).IsFixedLength();

            b.HasIndex(x => x.Email).IsUnique();

            b.HasMany(x => x.DeviceTokens)
             .WithOne()
             .HasForeignKey(dt => dt.TravelerId)
             .OnDelete(DeleteBehavior.Cascade);

            b.HasOne<Country>()
             .WithMany()
             .HasForeignKey(x => x.NationalityCountryCode)
             .HasPrincipalKey(c => c.Code)
             .OnDelete(DeleteBehavior.Restrict);

            b.HasOne<Country>()
             .WithMany()
             .HasForeignKey(x => x.ResidenceCountryCode)
             .HasPrincipalKey(c => c.Code)
             .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
