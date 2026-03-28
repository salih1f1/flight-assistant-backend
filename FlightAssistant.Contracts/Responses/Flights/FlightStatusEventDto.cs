namespace FlightAssistant.Contracts.Responses.Flights
{
    public record FlightStatusEventDto(
        long Id,
        string StatusCode,
        string StatusName,
        DateTime EffectiveAtUtc,
        string Source,
        string? Gate,
        string? Terminal,
        string? BaggageClaim,
        string? Remarks
    );
}
