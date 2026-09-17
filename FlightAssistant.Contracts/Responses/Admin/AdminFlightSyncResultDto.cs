namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>
    /// What <c>POST /admin/flights/{id}/sync</c> just did, and the sync plan it left behind (#349
    /// §2.6). Replaces the untyped <c>{ updated }</c> the route used to return: that collapsed six
    /// distinct situations into one boolean, so the panel could not say whether a sync made no call
    /// because nobody tracks the flight, called and found no row for the service date yet, or called
    /// and found nothing new. The traveler route keeps <c>{ updated }</c>.
    /// </summary>
    /// <param name="Outcome">A <c>FlightSyncOutcome</c> name: <c>StatusUpdated</c>, <c>NoChange</c>,
    /// <c>NoDataYet</c>, <c>ScheduleUpdated</c>, <c>NotInSchedule</c> or <c>NotTracked</c>. Sent as
    /// the name, not the number, so adding an outcome can't renumber the others.
    /// <c>ScheduleUpdated</c> and <c>NotInSchedule</c> cannot occur until #444 implements the daily
    /// schedule check.</param>
    /// <param name="SyncMode">The <c>FlightSyncMode</c> name now stored on the flight
    /// (<c>Fast</c>, <c>Live</c>, <c>Schedule</c>, <c>Stopped</c>), or null when the flight has no
    /// plan because no traveler tracks it.</param>
    /// <param name="ScheduleMissingSinceUtc">When the flight first went missing from the schedule;
    /// always null until #444 implements the check that sets it.</param>
    public record AdminFlightSyncResultDto(
        string Outcome,
        DateTime? LastSyncedUtc,
        DateTime? NextSyncDueUtc,
        string? SyncMode,
        DateTime? ScheduleMissingSinceUtc
    );
}
