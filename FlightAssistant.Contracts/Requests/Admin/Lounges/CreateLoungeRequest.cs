namespace FlightAssistant.Contracts.Requests.Admin.Lounges
{
    public record CreateLoungeRequest(
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
