namespace FlightAssistant.Contracts.Responses.Admin
{
    public record AdminLoungeDto(
        int Id,
        int AirportId,
        string Name,
        string? Terminal,
        string? LocationHint,
        string? Hours,
        bool IsAirside,
        bool IsDomesticOnly,
        bool IsInternationalOnly,
        string? Amenities
    );
}
