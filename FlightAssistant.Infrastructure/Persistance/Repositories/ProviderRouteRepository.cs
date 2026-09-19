using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Domain.Providers.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    /// <summary>Upserts <c>provider_routes</c>. One row per key — four at most — so no paging or
    /// filtering is needed.</summary>
    public sealed class ProviderRouteRepository : IProviderRouteRepository
    {
        private readonly FlightAssistantDbContext _db;

        public ProviderRouteRepository(FlightAssistantDbContext db) => _db = db;

        public async Task UpsertAsync(ProviderRoute row, CancellationToken ct)
        {
            var existing = await _db.ProviderRoutes.FirstOrDefaultAsync(r => r.Job == row.Job, ct);

            if (existing is null)
            {
                _db.ProviderRoutes.Add(row);
            }
            else
            {
                existing.Providers = row.Providers;
                existing.ChangedUtc = row.ChangedUtc;
                existing.ChangedBy = row.ChangedBy;
                existing.Reason = row.Reason;
            }

            await _db.SaveChangesAsync(ct);
        }
    }
}
