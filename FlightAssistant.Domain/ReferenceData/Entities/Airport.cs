using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.ReferenceData.Entities
{
    public class Airport : SeedableEntity
    {
        public int Id { get; init; }
        public string Iata { get; init; } = default!;
        public string? Icao { get; init; }
        public string Name { get; init; } = default!;
        public string City { get; init; } = default!;
        public string CountryCode { get; init; } = default!;

        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
        public int? ElevationFt { get; init; }
        public string? Timezone { get; init; }
    }
}
