namespace FlightAssistant.Contracts.Requests.Admin.Airports
{
    public record CreateAirportRequest(
        string Iata,
        string? Icao,
        string Name,
        string City,
        string CountryCode,
        double? Latitude,
        double? Longitude,
        int? ElevationFt,
        string? Timezone
    );
}
