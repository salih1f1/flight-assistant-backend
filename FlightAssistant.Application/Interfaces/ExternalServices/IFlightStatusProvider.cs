namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    public record FlightStatusResult(
        string StatusCode,           // maps to FlightStatus.Code (e.g. "SCH", "DEP", "ARR", "DLY")
        DateTime EffectiveAtUtc,
        DateTime? EstimatedDepartureUtc,
        DateTime? EstimatedArrivalUtc,
        DateTime? ActualDepartureUtc,
        DateTime? ActualArrivalUtc,
        string? Gate,
        string? Terminal,
        string? BaggageClaim,
        string? EquipmentIcao,
        string? ExternalId,
        string? RawPayloadJson,
        // The provider's current scheduled departure (#352). A delay's minutes count from it, and (#399)
        // the tracked flight's stored schedule is refreshed from it too.
        DateTime? ScheduledDepartureUtc = null,
        // The provider's current scheduled arrival (#399), the counterpart of ScheduledDepartureUtc.
        // A retimed flight's stored schedule is refreshed from both, so the sync planner's windows and
        // the tracking cap's stop rule no longer work from the times the flight was first tracked with.
        DateTime? ScheduledArrivalUtc = null
    );

    /// <summary>
    /// The provider names recorded in <c>flight_status_events.Source</c>. Every path that records a status
    /// must pass the same string: it is hashed into the event's dedup hash (#351), so a caller spelling it
    /// differently would match no stored hash and record a second copy of every status.
    /// </summary>
    public static class FlightStatusSources
    {
        /// <summary>FlightAware AeroAPI. <b>Never change this value</b> — it is `"AeroAPI"`, not the
        /// `"AeroApi"` spelling used for the config section and the named HTTP client, and every stored hash
        /// was computed from it.</summary>
        public const string AeroApi = "AeroAPI";
    }

    /// <summary>Who a status call is for (#375). A scheduled poll isn't retried on a transient
    /// failure, because AeroAPI bills failed calls and the sync loop's next due sync is the retry. A call a
    /// person is waiting on keeps the provider's retries.</summary>
    public enum StatusCallKind
    {
        Interactive,
        ScheduledPoll
    }

    public interface IFlightStatusProvider
    {
        string ProviderName { get; }
        Task<FlightStatusResult?> GetStatusAsync(string flightNumber, DateOnly serviceDate, StatusCallKind kind, CancellationToken ct = default);
    }
}
