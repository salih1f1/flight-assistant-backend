namespace FlightAssistant.Contracts.Requests.Admin.Airlines
{
    public record UpdateAirlineRequest(
        string? Iata,
        string? Icao,
        string Name,
        string CountryCode,
        string? AllianceCode
    );
}
