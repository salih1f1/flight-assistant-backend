namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    /// <summary>
    /// One physical departure. Airlines sell the same aircraft under several flight numbers
    /// (TK1028 is also 6E4120, MS9668, RJ3710 and SQ6228), so a result groups every number that
    /// covers the same departure rather than repeating it once per marketing carrier.
    /// </summary>
    /// <param name="FlightNumbers">Every IATA flight code on this departure, uppercase and sorted.
    /// Always contains at least one entry, and always contains the traveler's own number whichever
    /// carrier issued their ticket.</param>
    /// <param name="OperatingFlightNumber">The carrier actually flying the aircraft, when the
    /// provider discloses it. <c>null</c> for future-dated searches: the AviationStack schedules
    /// endpoint carries no codeshare marker, and guessing would be wrong often enough to matter.</param>
    /// <param name="AirlineIata">Operating airline's IATA code, when known. <c>null</c> whenever
    /// <paramref name="OperatingFlightNumber"/> is.</param>
    public record FlightSearchResult(
        IReadOnlyList<string> FlightNumbers,
        string? OperatingFlightNumber,
        string? AirlineIata,
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
        /// Searches for scheduled flights matching the given <paramref name="criteria"/>, returning
        /// one result per physical departure.
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
