using FlightAssistant.Domain.Flights.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Flights
{
    public sealed class FlightStatusConfiguration : IEntityTypeConfiguration<FlightStatus>
    {
        public void Configure(EntityTypeBuilder<FlightStatus> b)
        {
            b.ToTable("flight_statuses");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).IsRequired().HasMaxLength(32);
            b.Property(x => x.Name).IsRequired().HasMaxLength(64);
            b.Property(x => x.Description).HasMaxLength(4000);

            b.HasIndex(x => x.Code).IsUnique();
        }
    }
}
