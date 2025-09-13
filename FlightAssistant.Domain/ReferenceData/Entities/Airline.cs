using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Flights.Entities;

namespace FlightAssistant.Domain.ReferenceData.Entities
{
    public class Airline : SeadableEntity
    {
        public int Id { get; private set; }
        public string Iata { get; private set; } = default!;
        public string? Icao { get; private set; }
        public string Name { get; private set; } = default!;
        public string CountryCode { get; private set; } = default!;

        public string? AllianceCode { get; private set; }
        public Alliance? Alliance { get; private set; }

        public ICollection<Flight> Flights { get; private set; } = new List<Flight>();
    }
}
