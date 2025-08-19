using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Flights
{
    public sealed class FlightConfiguration : IEntityTypeConfiguration<Flight>
    {
        public void Configure(EntityTypeBuilder<Flight> b)
        {
            b.ToTable("flights");
            b.HasKey(x => x.Id);

            b.Property(x => x.FlightNumber).IsRequired().HasMaxLength(8);
            b.Property(x => x.EquipmentAircraftIcao).HasMaxLength(8);
            b.Property(x => x.ExternalId).HasMaxLength(64);

            b.Property(x => x.ServiceDateUtc).HasColumnType("date");

            // indexes for fast lookups
            b.HasIndex(x => new { x.AirlineId, x.FlightNumber, x.ServiceDateUtc }).HasDatabaseName("ix_flight_lookup");
            b.HasIndex(x => new { x.DepartureAirportId, x.ServiceDateUtc }).HasDatabaseName("ix_flight_dep_day");
            b.HasIndex(x => new { x.ArrivalAirportId, x.ServiceDateUtc }).HasDatabaseName("ix_flight_arr_day");

            b.HasOne<Airline>().WithMany().HasForeignKey(x => x.AirlineId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Airport>().WithMany().HasForeignKey(x => x.DepartureAirportId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Airport>().WithMany().HasForeignKey(x => x.ArrivalAirportId).OnDelete(DeleteBehavior.Restrict);
        }
    }
}
