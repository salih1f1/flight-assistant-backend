namespace FlightAssistant.Contracts.Responses.Flights
{
    /// <summary>
    /// A page of tracked flights, plus the traveler's slot usage against the tracking cap (#397).
    /// The first four members match <c>PagedResult&lt;T&gt;</c> exactly, so this is additive for
    /// existing clients.
    /// </summary>
    public record TrackedFlightsDto(
        IReadOnlyList<FlightDto> Items,
        int Total,
        int Page,
        int PageSize,
        int ActiveTrackedFlights,
        int TrackingCap
    );
}
