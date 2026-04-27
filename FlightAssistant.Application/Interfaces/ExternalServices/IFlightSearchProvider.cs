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

    public interface IFlightSearchProvider
    {
        /// <summary>
        /// Searches for scheduled flights between two airports on a given date.
        /// Results are not persisted — callers use them to populate a picker before tracking.
        /// Returns an empty list (not null) if no flights are found or the provider is unavailable.
        /// </summary>
        Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            string departureIata,
            string arrivalIata,
            DateOnly date,
            CancellationToken ct = default);
    }
}
