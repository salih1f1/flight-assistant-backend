namespace FlightAssistant.Contracts.Responses.Admin
{
    public record AirportAdminDto(
        int Id,
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
