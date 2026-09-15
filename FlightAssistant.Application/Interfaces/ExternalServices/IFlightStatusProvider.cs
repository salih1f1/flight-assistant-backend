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
        string? RawPayloadJson
    );

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
