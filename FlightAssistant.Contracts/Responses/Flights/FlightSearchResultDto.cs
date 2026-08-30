namespace FlightAssistant.Contracts.Responses.Flights
{
    /// <summary>
    /// One scheduled flight row. Shaped identically to <c>TrackFlightRequest</c> — same six
    /// fields, same names, same order — so a search result can be posted straight to
    /// <c>POST /flights</c> to track it, with no client-side transformation.
    /// </summary>
    public record FlightSearchResultDto(
        string FlightNumber,
        DateOnly ServiceDate,
        string DepartureAirportIata,
        string ArrivalAirportIata,
        DateTime ScheduledDepartureUtc,
        DateTime ScheduledArrivalUtc
    );
}
