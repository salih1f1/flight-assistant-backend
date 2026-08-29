namespace FlightAssistant.Contracts.Responses.Flights
{
    /// <summary>
    /// One physical departure. The same aircraft is sold under several flight numbers, so
    /// <paramref name="FlightNumbers"/> lists all of them rather than returning a near-duplicate
    /// row per marketing carrier.
    /// </summary>
    /// <param name="FlightNumbers">Every IATA flight code on this departure, e.g.
    /// <c>["6E4120", "MS9668", "RJ3710", "SQ6228", "TK1028"]</c>. Uppercase and sorted.</param>
    /// <param name="OperatingFlightNumber">The carrier actually operating the aircraft, when known.
    /// <c>null</c> for future dates, where the provider does not disclose it.</param>
    /// <param name="AirlineIata">Operating airline's IATA code, when known.</param>
    public record FlightSearchResultDto(
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
}
