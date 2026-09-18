using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.DataSources.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    /// <summary>
    /// Reads and upserts <c>data_source_status</c>. The table holds one row per catalog key — nine
    /// today — so <see cref="GetAllAsync"/> reads the lot untracked rather than paging.
    /// </summary>
    public sealed class DataSourceStatusRepository : IDataSourceStatusRepository
    {
        private readonly FlightAssistantDbContext _db;

        public DataSourceStatusRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<IReadOnlyList<DataSourceStatus>> GetAllAsync(CancellationToken ct) =>
            await _db.DataSourceStatuses.AsNoTracking().ToListAsync(ct);

        public Task<DataSourceStatus?> GetAsync(string key, CancellationToken ct) =>
            _db.DataSourceStatuses.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, ct);

        public async Task UpsertAsync(DataSourceStatus row, CancellationToken ct)
        {
            var existing = await _db.DataSourceStatuses.FirstOrDefaultAsync(r => r.Key == row.Key, ct);

            if (existing is null)
            {
                _db.DataSourceStatuses.Add(row);
            }
            else
            {
                existing.LastOutcome = row.LastOutcome;
                existing.LastOutcomeUtc = row.LastOutcomeUtc;
                existing.LastOkUtc = row.LastOkUtc;
                existing.LastError = row.LastError;
                existing.ConsecutiveFailures = row.ConsecutiveFailures;
                // Probe fields are only written by the probe path, so an observation flush must
                // not blank them.
                if (row.LastProbeUtc is not null)
                {
                    existing.LastProbeUtc = row.LastProbeUtc;
                    existing.LastProbeBy = row.LastProbeBy;
                }
            }

            await _db.SaveChangesAsync(ct);
        }
    }
}
