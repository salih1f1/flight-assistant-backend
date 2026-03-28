namespace FlightAssistant.Contracts.Requests.Admin.Airlines
{
    public record CreateAirlineRequest(
        string? Iata,
        string? Icao,
        string Name,
        string CountryCode,
        string? AllianceCode
    );
}
