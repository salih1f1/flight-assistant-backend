using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Flights.Entities;

namespace FlightAssistant.Domain.ReferenceData.Entities
{
    public class Airline : SeedableEntity
    {
        public int Id { get; init; }
        public string? Iata { get; init; } = default!;
        public string? Icao { get; init; } = default!;
        public string Name { get; init; } = default!;
        public string CountryCode { get; init; } = default!;

        public string? AllianceCode { get; init; }
        public Alliance? Alliance { get; private set; }

        public ICollection<Flight> Flights { get; private set; } = new List<Flight>();
    }
}
