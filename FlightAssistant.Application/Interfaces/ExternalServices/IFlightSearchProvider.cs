namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    public record FlightSearchResult(
        string FlightNumber,
        string AirlineIata,
        string DepartureIata,
        string ArrivalIata,
        DateOnly ServiceDate,
        DateTime ScheduledDepartureUtc,
        DateTime ScheduledArrivalUtc,
        string? EquipmentIcao
    );

    /// <summary>
    /// Criteria for a flight search. A search is anchored on a <see cref="Date"/> plus either a
    /// full route (<see cref="DepartureIata"/> and <see cref="ArrivalIata"/>) or a specific
    /// <see cref="FlightNumber"/>. Airport codes are 3-letter IATA codes.
    /// </summary>
    public record FlightSearchCriteria(
        string? DepartureIata,
        string? ArrivalIata,
        DateOnly Date,
        string? FlightNumber);

    public interface IFlightSearchProvider
    {
        /// <summary>
        /// Searches for scheduled flights matching the given <paramref name="criteria"/>.
        /// Results are not persisted — callers use them to populate a picker before tracking.
        /// Returns an empty list (not null) when the provider responds successfully but no flights match.
        /// Throws <see cref="FlightAssistant.Domain.Common.Exceptions.DomainException"/> when the
        /// provider cannot be reached or returns an error (502/503/500 depending on the cause) —
        /// this is distinct from a successful empty result.
        /// </summary>
        Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default);
    }
}
