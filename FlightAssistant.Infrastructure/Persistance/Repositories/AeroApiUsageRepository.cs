using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Configuration.Entities;
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

            // Both the total and the billable (non-Throttled) count come out of ONE grouped scan —
            // the same GROUP BY with a conditional SUM alongside the COUNT — rather than a second
            // round trip over the same rows. SUM(CASE WHEN … THEN 0 ELSE 1 END) is used in
            // preference to Count(predicate) because it is the form every provider has translated
            // for as long as EF Core has had GroupBy support.
            var templateCounts = await rangeQuery
                .GroupBy(c => c.EndpointTemplate)
                .Select(g => new
                {
                    Template = g.Key,
                    Count = g.Count(),
                    BillableCount = g.Sum(c => c.Outcome == AeroApiCallOutcome.Throttled ? 0 : 1),
                })
                .ToListAsync(ct);

            var callsByTemplate = templateCounts.ToDictionary(x => x.Template, x => x.Count);
            var billableCallsByTemplate = templateCounts.ToDictionary(x => x.Template, x => x.BillableCount);

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

            // Not `StatusCode == 502`: AeroAPI never sends one. A 502 out of THIS app is a mapping
            // applied to a call that was unreachable or came back unparseable, and the recording
            // handler stores exactly that shape — Outcome=Failed with no status code at all.
            // Matching on the upstream status would report 0 while clients were being served 502s.
            var transportFailureCount = await _db.AeroApiCalls.CountAsync(c =>
                c.CalledUtc >= last7DaysStartUtc && c.CalledUtc < last7DaysEndUtc
                && c.Outcome == AeroApiCallOutcome.Failed && c.StatusCode == null, ct);

            return new AeroApiUsageRawData(
                callsByTemplate,
                billableCallsByTemplate,
                callsByOutcome,
                callsByDay,
                callsByTemplateForCurrentMonth,
                status429Count,
                transportFailureCount);
        }
    }
}
