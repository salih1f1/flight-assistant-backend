namespace FlightAssistant.Contracts.Responses.Admin
{
    public record AdminFlightDto(
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
        // The flight's sync plan (#349 step 3, #443). These five live on the entity and were
        // previously visible only through the raw data browser, so no admin surface could say how
        // actively a flight is polled or when it is next due. SyncMode is the FlightSyncMode name
        // (Fast, Live, Schedule, Stopped), null when no traveler tracks the flight;
        // ScheduleMissingSinceUtc stays null until #444 implements the check that sets it.
        DateTime? LastSyncedUtc,
        DateTime? NextSyncDueUtc,
        string? SyncMode,
        DateTime? ScheduleMissingSinceUtc,
        DateTime? CancelledAtUtc
    );
}
