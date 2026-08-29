namespace FlightAssistant.Application.Interfaces.Repositories
{
    /// <summary>
    /// Reference-data lookups (IATA ↔ id). Single port avoids extra interfaces while we only
    /// need primitive resolution.
    /// </summary>
    /// <remarks>
    /// Replace or split into per-aggregate repositories if airline and airport gain real domain
    /// behavior and persistence as aggregate roots.
    /// </remarks>
    public interface IRefDataRepository
    {
        Task<int?> GetAirlineIdByIataAsync(string iata, CancellationToken ct = default);
        Task<int?> GetAirportIdByIataAsync(string iata, CancellationToken ct = default);
        Task<string?> GetAirlineIataByIdAsync(int id, CancellationToken ct = default);
        Task<string?> GetAirportIataByIdAsync(int id, CancellationToken ct = default);

        /// <summary>
        /// The airport's IANA timezone (e.g. <c>"Europe/Sofia"</c>), or null when the airport is
        /// unknown or has no timezone recorded. Needed to interpret provider timetables, which
        /// give airport-local times with no UTC offset.
        /// </summary>
        Task<string?> GetAirportTimezoneByIataAsync(string iata, CancellationToken ct = default);
        Task<IReadOnlyDictionary<int, string>> GetAirportIataMapAsync(IReadOnlyList<int> ids, CancellationToken ct = default);
        Task<IReadOnlyDictionary<int, string>> GetAirlineIataMapAsync(IReadOnlyList<int> ids, CancellationToken ct = default);
        Task<IReadOnlyDictionary<int, string>> GetLoungeProviderCodeMapAsync(IReadOnlyList<int> ids, CancellationToken ct = default);
    }
}
