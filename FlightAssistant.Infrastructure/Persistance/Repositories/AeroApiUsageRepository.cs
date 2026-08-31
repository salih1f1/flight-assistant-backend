using FlightAssistant.Application.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    /// <summary>
    /// Backs the AeroAPI usage rollup (<c>GET /admin/usage/aeroapi</c>). Every count is grouped by
    /// EF Core's SQL translation — nothing here pulls <c>aeroapi_calls</c> rows into memory to group
    /// them client-side, since the table grows unbounded until the retention prune runs. Pricing and
    /// budget arithmetic live in <c>GetAeroApiUsageQueryHandler</c>, not here.
    /// </summary>
    public sealed class AeroApiUsageRepository : IAeroApiUsageRepository
    {
        private readonly FlightAssistantDbContext _db;
        public AeroApiUsageRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<AeroApiUsageRawData> GetUsageRollupAsync(
            DateTime rangeFromUtc,
            DateTime rangeToUtc,
            DateTime currentMonthStartUtc,
            DateTime currentMonthEndUtc,
            DateTime last7DaysStartUtc,
            DateTime last7DaysEndUtc,
            CancellationToken ct = default)
        {
            var rangeQuery = _db.AeroApiCalls
                .Where(c => c.CalledUtc >= rangeFromUtc && c.CalledUtc < rangeToUtc);

            var callsByTemplate = await rangeQuery
                .GroupBy(c => c.EndpointTemplate)
                .Select(g => new { Template = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Template, x => x.Count, ct);

            var callsByOutcome = await rangeQuery
                .GroupBy(c => c.Outcome)
                .Select(g => new { Outcome = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Outcome, x => x.Count, ct);

            var callsByDayRaw = await rangeQuery
                .GroupBy(c => c.CalledUtc.Date)
                .Select(g => new { Day = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var callsByDay = callsByDayRaw.ToDictionary(x => DateOnly.FromDateTime(x.Day), x => x.Count);

            var callsByTemplateForCurrentMonth = await _db.AeroApiCalls
                .Where(c => c.CalledUtc >= currentMonthStartUtc && c.CalledUtc < currentMonthEndUtc)
                .GroupBy(c => c.EndpointTemplate)
                .Select(g => new { Template = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Template, x => x.Count, ct);

            var status429Count = await _db.AeroApiCalls.CountAsync(c =>
                c.CalledUtc >= last7DaysStartUtc && c.CalledUtc < last7DaysEndUtc && c.StatusCode == 429, ct);
            var status502Count = await _db.AeroApiCalls.CountAsync(c =>
                c.CalledUtc >= last7DaysStartUtc && c.CalledUtc < last7DaysEndUtc && c.StatusCode == 502, ct);

            return new AeroApiUsageRawData(
                callsByTemplate,
                callsByOutcome,
                callsByDay,
                callsByTemplateForCurrentMonth,
                status429Count,
                status502Count);
        }
    }
}
