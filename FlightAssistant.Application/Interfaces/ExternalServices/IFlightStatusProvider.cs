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

    public interface IFlightStatusProvider
    {
        string ProviderName { get; }
        Task<FlightStatusResult?> GetStatusAsync(string flightNumber, DateOnly serviceDate, CancellationToken ct = default);
    }
}
