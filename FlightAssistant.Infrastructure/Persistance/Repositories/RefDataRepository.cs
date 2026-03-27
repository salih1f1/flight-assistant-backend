using FlightAssistant.Application.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    /// <summary>
    /// Read-only IATA/id lookups for airlines and airports. Kept in one place because this
    /// bounded context treats them as shared reference data: we only need keys for joins and
    /// validation, not full <c>Airline</c>/<c>Airport</c> aggregates or write operations.
    /// </summary>
    /// <remarks>
    /// If the domain grows—e.g. airline/airport become true aggregate roots with invariants,
    /// lifecycle, or commands that create/update them—split into <c>IAirlineRepository</c> /
    /// <c>IAirportRepository</c> (or a dedicated reference-data module) and return domain
    /// entities from those repositories; keep thin lookup methods only where handlers still
    /// need id resolution without loading full graphs.
    /// </remarks>
    public class RefDataRepository : IRefDataRepository
    {
        private readonly FlightAssistantDbContext _db;
        public RefDataRepository(FlightAssistantDbContext db) => _db = db;

        public Task<int?> GetAirlineIdByIataAsync(string iata, CancellationToken ct = default) =>
            _db.Airlines
                .Where(a => a.Iata == iata.ToUpperInvariant())
                .Select(a => (int?)a.Id)
                .FirstOrDefaultAsync(ct);

        public Task<int?> GetAirportIdByIataAsync(string iata, CancellationToken ct = default) =>
            _db.Airports
                .Where(a => a.Iata == iata.ToUpperInvariant())
                .Select(a => (int?)a.Id)
                .FirstOrDefaultAsync(ct);

        public Task<string?> GetAirlineIataByIdAsync(int id, CancellationToken ct = default) =>
            _db.Airlines
                .Where(a => a.Id == id)
                .Select(a => (string?)a.Iata)
                .FirstOrDefaultAsync(ct);

        public Task<string?> GetAirportIataByIdAsync(int id, CancellationToken ct = default) =>
            _db.Airports
                .Where(a => a.Id == id)
                .Select(a => (string?)a.Iata)
                .FirstOrDefaultAsync(ct);
    }
}
