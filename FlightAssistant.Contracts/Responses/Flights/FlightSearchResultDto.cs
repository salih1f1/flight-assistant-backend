namespace FlightAssistant.Contracts.Responses.Flights
{
    public record FlightSearchResultDto(
        string FlightNumber,
        string AirlineIata,
        string DepartureIata,
        string ArrivalIata,
        DateOnly ServiceDate,
        DateTime ScheduledDepartureUtc,
        DateTime ScheduledArrivalUtc,
        string? EquipmentIcao
    );
}
