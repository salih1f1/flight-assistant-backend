namespace FlightAssistant.Contracts.Requests.Flights
{
    public record TrackFlightRequest(
        string FlightNumber,
        DateOnly ServiceDate,
        string DepartureAirportIata,
        string ArrivalAirportIata,
        DateTime ScheduledDepartureUtc,
        DateTime ScheduledArrivalUtc
    );
}
