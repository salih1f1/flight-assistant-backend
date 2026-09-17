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

    /// <summary>The flight with its current status and recent status-event history, most recently
    /// recorded first (by id, #351 — not by effective time, which can move backwards).
    /// Backs <c>GET /admin/flights/{id}/detail</c>. <c>StatusHistory</c> is capped, not the full
    /// history — see the endpoint's Swagger description for the limit. <c>StatusHistoryTruncated</c>
    /// is true when the cap actually cut rows off; when it is, the full history is available via
    /// <c>GET /admin/data/flight-status-events</c>.</summary>
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
        AdminFlightStatusEventDto? CurrentStatus,
        IReadOnlyList<AdminFlightStatusEventDto> StatusHistory,
        bool StatusHistoryTruncated,
        // The flight's sync plan (#349 step 3, #443). These five live on the entity and were
        // previously visible only through the raw data browser, so no admin surface could say how
        // actively a flight is polled or when it is next due. SyncMode is the FlightSyncMode name
        // (Fast, Live, Schedule, Stopped), null when no traveler tracks the flight;
        // ScheduleMissingSinceUtc stays null until #444 implements the check that sets it.
        DateTime? LastSyncedUtc,
        DateTime? NextSyncDueUtc,
        string? SyncMode,
        DateTime? ScheduleMissingSinceUtc,
        DateTime? CancelledAtUtc);
}
