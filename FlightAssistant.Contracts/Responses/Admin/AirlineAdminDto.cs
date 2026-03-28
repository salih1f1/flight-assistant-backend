namespace FlightAssistant.Contracts.Responses.Admin
{
    public record AirlineAdminDto(
        int Id,
        string? Iata,
        string? Icao,
        string Name,
        string CountryCode,
        string? AllianceCode
    );
}
