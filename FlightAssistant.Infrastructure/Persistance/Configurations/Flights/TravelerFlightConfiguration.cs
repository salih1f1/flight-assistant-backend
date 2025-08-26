using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Flights
{
    public sealed class TravelerFlightConfiguration : IEntityTypeConfiguration<TravelerFlight>
    {
        public void Configure(EntityTypeBuilder<TravelerFlight> b)
        {
            b.ToTable("traveler_flights");
            b.HasKey(x => x.Id);

            b.HasIndex(x => new { x.TravelerId, x.FlightId }).IsUnique();

            b.HasOne<Traveler>().WithMany().HasForeignKey(x => x.TravelerId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Flight>().WithMany().HasForeignKey(x => x.FlightId).OnDelete(DeleteBehavior.Cascade);
        }
    }
}
