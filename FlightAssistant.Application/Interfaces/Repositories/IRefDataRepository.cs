namespace FlightAssistant.Application.Interfaces.Repositories
{
    /// <summary>Read-only lookups for airline/airport reference data needed by handlers.</summary>
    public interface IRefDataRepository
    {
        Task<int?> GetAirlineIdByIataAsync(string iata, CancellationToken ct = default);
        Task<int?> GetAirportIdByIataAsync(string iata, CancellationToken ct = default);
        Task<string?> GetAirlineIataByIdAsync(int id, CancellationToken ct = default);
        Task<string?> GetAirportIataByIdAsync(int id, CancellationToken ct = default);
    }
}
