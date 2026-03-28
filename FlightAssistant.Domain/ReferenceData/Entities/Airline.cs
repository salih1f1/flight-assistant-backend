using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Flights.Entities;

namespace FlightAssistant.Domain.ReferenceData.Entities
{
    public class Airline : SeedableEntity
    {
        public int Id { get; init; }
        public string? Iata { get; private set; } = default!;
        public string? Icao { get; private set; } = default!;
        public string Name { get; private set; } = default!;
        public string CountryCode { get; private set; } = default!;

        public string? AllianceCode { get; private set; }
        public Alliance? Alliance { get; private set; }

        public ICollection<Flight> Flights { get; private set; } = new List<Flight>();

        public static Airline Create(string? iata, string? icao, string name, string countryCode, string? allianceCode = null) =>
            new()
            {
                Iata = iata,
                Icao = icao,
                Name = name,
                CountryCode = countryCode,
                AllianceCode = allianceCode,
                SourceType = "admin"
            };

        public void Update(string? iata, string? icao, string name, string countryCode, string? allianceCode)
        {
            Iata = iata;
            Icao = icao;
            Name = name;
            CountryCode = countryCode;
            AllianceCode = allianceCode;
        }
    }
}
