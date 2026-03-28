namespace FlightAssistant.Contracts.Responses.Flights
{
    public record FlightDto(
        int FlightId,
        string AirlineIata,
        string FlightNumber,
        DateOnly ServiceDate,
        string DepartureAirportIata,
        string ArrivalAirportIata,
        DateTime ScheduledDepartureUtc,
        DateTime ScheduledArrivalUtc,
        DateTime? EstimatedDepartureUtc,
        DateTime? EstimatedArrivalUtc,
        DateTime? ActualDepartureUtc,
        DateTime? ActualArrivalUtc,
        string? LatestStatusCode,
        bool Notify
    );
}
