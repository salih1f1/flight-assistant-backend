using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.ReferenceData.Entities
{
    public class Airport : SeedableEntity
    {
        public int Id { get; init; }
        public string Iata { get; private set; } = default!;
        public string? Icao { get; private set; }
        public string Name { get; private set; } = default!;
        public string City { get; private set; } = default!;
        public string CountryCode { get; private set; } = default!;

        public double? Latitude { get; private set; }
        public double? Longitude { get; private set; }
        public int? ElevationFt { get; private set; }
        public string? Timezone { get; private set; }

        private Airport() { } // EF

        public static Airport Create(string iata, string? icao, string name, string city, string countryCode,
            double? latitude = null, double? longitude = null, int? elevationFt = null, string? timezone = null) =>
            new()
            {
                Iata = iata.ToCanonicalCode(),
                Icao = icao.ToCanonicalCodeOrNull(),
                Name = name,
                City = city,
                CountryCode = countryCode.ToCanonicalCode(),
                Latitude = latitude,
                Longitude = longitude,
                ElevationFt = elevationFt,
                Timezone = timezone,
                SourceType = "admin"
            };

        public void Update(string iata, string? icao, string name, string city, string countryCode,
            double? latitude, double? longitude, int? elevationFt, string? timezone)
        {
            Iata = iata.ToCanonicalCode();
            Icao = icao.ToCanonicalCodeOrNull();
            Name = name;
            City = city;
            CountryCode = countryCode.ToCanonicalCode();
            Latitude = latitude;
            Longitude = longitude;
            ElevationFt = elevationFt;
            Timezone = timezone;
        }
    }
}
