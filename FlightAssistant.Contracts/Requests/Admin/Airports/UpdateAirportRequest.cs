namespace FlightAssistant.Contracts.Requests.Admin.Airports
{
    public record UpdateAirportRequest(
        string Iata,
        string? Icao,
        string Name,
        string City,
        string CountryCode,
        double? Latitude,
        double? Longitude,
        int? ElevationFt
    );
}
