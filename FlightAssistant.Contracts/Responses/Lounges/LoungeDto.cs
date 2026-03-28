namespace FlightAssistant.Contracts.Responses.Lounges
{
    public record LoungeDto(
        int Id,
        string Name,
        string? Terminal,
        string? LocationHint,
        string? Hours,
        bool IsAirside,
        bool IsDomesticOnly,
        bool IsInternationalOnly,
        string? Amenities,
        bool IsEligible,
        string? EligibleVia
    );
}
