namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    /// <summary>
    /// One scheduled flight row, shaped so it can be posted straight to <c>POST /flights</c>
    /// (<c>TrackFlightRequest</c>) with no client-side transformation — same six fields, same
    /// names, same order. Deliberately excludes any notion of an operating carrier or codeshare
    /// grouping: the provider returns one result per flight designator, and the airline is derived
    /// from <see cref="FlightNumber"/>'s own first two characters wherever it is needed downstream.
    /// </summary>
    public record FlightSearchResult(
        string FlightNumber,
        DateOnly ServiceDate,
        string DepartureAirportIata,
        string ArrivalAirportIata,
        DateTime ScheduledDepartureUtc,
        DateTime ScheduledArrivalUtc);

    /// <summary>
    /// Criteria for a flight search. A search is anchored on a <see cref="Date"/> plus either a
    /// full route (<see cref="DepartureIata"/> and <see cref="ArrivalIata"/>) or a specific
    /// <see cref="FlightNumber"/>. Airport codes are 3-letter IATA codes.
    /// </summary>
    /// <param name="IncludeCodeshares">When <c>true</c>, includes codeshare-marketed rows for the
    /// same physical departure alongside the operating flight's own row; when <c>false</c>, only
    /// the operating flight's row is returned.</param>
    public record FlightSearchCriteria(
        string? DepartureIata,
        string? ArrivalIata,
        DateOnly Date,
        string? FlightNumber,
        bool IncludeCodeshares = false);

    /// <summary>
    /// The provider's search horizon, exposed here (in the Application layer) so the query
    /// validator can bound requests against the real configured limits without depending on the
    /// Infrastructure-layer options class that owns them — Application never references
    /// Infrastructure. The concrete provider options type implements this interface.
    /// </summary>
    public interface IFlightSearchHorizonOptions
    {
        int MaxFutureMonths { get; }
        int MaxPastMonths { get; }
    }

    public interface IFlightSearchProvider
    {
        /// <summary>
        /// Searches for scheduled flights matching the given <paramref name="criteria"/>, returning
        /// one result per flight designator.
        /// Results are not persisted — callers use them to populate a picker before tracking.
        /// Returns an empty list (not null) when the provider responds successfully but no flights match.
        /// Throws <see cref="FlightAssistant.Domain.Common.Exceptions.DomainException"/> when the
        /// provider cannot be reached or returns an error — this is distinct from a successful
        /// empty result.
        /// </summary>
        Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default);
    }
}
