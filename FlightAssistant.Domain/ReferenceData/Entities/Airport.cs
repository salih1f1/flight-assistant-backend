using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.ReferenceData.Entities
{
    public class Airport : SeadableEntity
    {
        public int Id { get; private set; }
        public string Iata { get; private set; } = default!;
        public string? Icao { get; private set; }
        public string Name { get; private set; } = default!;
        public string City { get; private set; } = default!;
        public string CountryCode { get; private set; } = default!;

        public double? Latitude { get; private set; }
        public double? Longitude { get; private set; }
        public int? ElevationFt { get; private set; }
        public string? Timezone { get; private set; }
    }
}
