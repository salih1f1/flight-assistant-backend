namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>One row of a flight's status-event history. <c>StatusCode</c>/<c>StatusName</c> are
    /// this system's own resolved status; <c>ProviderRawStatus</c> is the provider's own status text
    /// as recorded at sync time, or null when none was recorded (e.g. a manually-created
    /// event).</summary>
    public record AdminFlightStatusEventDto(
        long Id,
        string StatusCode,
        string StatusName,
        string? ProviderRawStatus,
        DateTime EffectiveAtUtc,
        DateTime RecordedAtUtc,
        string Source);

    /// <summary>The flight with its current status and recent status-event history, newest first.
    /// Backs <c>GET /admin/flights/{id}/detail</c>. <c>StatusHistory</c> is capped, not the full
    /// history — see the endpoint's Swagger description for the limit.</summary>
    public record AdminFlightDetailDto(
        int Id,
        int AirlineId,
        string FlightNumber,
        int DepartureAirportId,
        int ArrivalAirportId,
        DateOnly ServiceDate,
        DateTime ScheduledDepartureUtc,
        DateTime ScheduledArrivalUtc,
        DateTime? EstimatedDepartureUtc,
        DateTime? EstimatedArrivalUtc,
        DateTime? ActualDepartureUtc,
        DateTime? ActualArrivalUtc,
        string? EquipmentAircraftIcao,
        string? ExternalId,
        AdminFlightStatusEventDto? CurrentStatus,
        IReadOnlyList<AdminFlightStatusEventDto> StatusHistory);
}
