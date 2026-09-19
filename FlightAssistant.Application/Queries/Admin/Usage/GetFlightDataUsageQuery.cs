using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.FlightSync;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Usage
{
    /// <summary>One operation's slice of the reported spend, straight from the provider.</summary>
    public sealed record FlightDataSpendOperationResult(
        string Operation,
        int Calls,
        int Pages,
        decimal CostUsd);

    /// <summary>
    /// Tracked flights grouped by the mode the loop has them in. <paramref name="Mode"/> is
    /// <c>null</c> for a flight that has never been planned — tracked by an older build, or a track
    /// racing the last untrack.
    /// </summary>
    public sealed record FlightDataSyncModeCountResult(string? Mode, int Flights);

    /// <summary>What the window's polls actually produced, per recording source.</summary>
    public sealed record FlightDataEventYieldResult(string Source, int Events, int DistinctFlights);

    /// <summary>
    /// Figures this report calculates rather than measures. Named separately, and never mixed into
    /// the spend block, because <b>AeroAPI exposes no per-request identifier</b>: FlightAware bills
    /// per page and bills failed calls, so nothing here can attribute a page to a flight. A local
    /// call-counting table was attempted and closed in PR #294 for exactly that reason. These are
    /// therefore whole-window spend divided by a whole-window denominator — useful for a trend and
    /// for comparing one month to the next, worthless as a per-flight invoice.
    /// </summary>
    public sealed record FlightDataDerivedResult(
        decimal? CostUsdPerActiveTrackedFlight,
        decimal? CostUsdPerRecordedEvent);

    /// <summary>
    /// AeroAPI spend for the window paired with what our own side of it looked like (#503) — the
    /// numbers every Phase 4 decision in #364 turns on, none of which the existing
    /// <c>GET /admin/usage/aeroapi</c> can answer on its own.
    /// </summary>
    /// <param name="ActiveTrackedFlights">Distinct flights the loop is still polling, recomputed
    /// from times and status — the same authoritative rule the per-traveler cap enforces, not the
    /// stored mode.</param>
    /// <param name="ByStoredSyncMode">Composition by <b>stored</b> mode, which may be up to one
    /// loop interval stale and is what the loop has actually been doing. Its total can differ
    /// slightly from <paramref name="ActiveTrackedFlights"/>; that is expected, not a defect.</param>
    public sealed record FlightDataUsageResult(
        DateTimeOffset RangeStartUtc,
        DateTimeOffset RangeEndUtc,
        int TotalCalls,
        int TotalPages,
        decimal TotalCostUsd,
        decimal MonthlyBudgetUsd,
        IReadOnlyList<FlightDataSpendOperationResult> ByOperation,
        int ActiveTrackedFlights,
        IReadOnlyList<FlightDataSyncModeCountResult> ByStoredSyncMode,
        int TotalEventsRecorded,
        IReadOnlyList<FlightDataEventYieldResult> EventYield,
        FlightDataDerivedResult Derived);

    /// <summary>
    /// Both bounds are optional and default to the current UTC month to date, resolved by the same
    /// <c>UsageRange</c> the AeroAPI usage endpoint uses — one copy of that rule, so the two reports
    /// can never disagree about which window they are describing or share a range that misses the
    /// cache.
    /// </summary>
    public sealed record GetFlightDataUsageQuery(
        DateTimeOffset? StartUtc = null,
        DateTimeOffset? EndUtc = null) : IRequest<FlightDataUsageResult>;

    /// <summary>
    /// Reuses <see cref="GetAeroApiUsageQueryValidator"/>'s rule by delegating to a query of that
    /// shape, rather than restating the one-year history cap and the inverted-range check — the
    /// drift CLAUDE.md names for any rule with two homes.
    /// </summary>
    public sealed class GetFlightDataUsageQueryValidator : AbstractValidator<GetFlightDataUsageQuery>
    {
        public GetFlightDataUsageQueryValidator(TimeProvider timeProvider)
        {
            RuleFor(q => q)
                .Custom((query, context) =>
                {
                    var inner = new GetAeroApiUsageQuery(query.StartUtc, query.EndUtc);
                    var result = new GetAeroApiUsageQueryValidator(timeProvider).Validate(inner);
                    foreach (var failure in result.Errors)
                        context.AddFailure(failure);
                });
        }
    }

    public sealed class GetFlightDataUsageQueryHandler(
        IAeroApiAccountUsageProvider usageProvider,
        ICacheService cache,
        IFlightRepository flights,
        IAeroApiBudgetOptions budgetOptions,
        IFlightSyncOptions syncOptions,
        TimeProvider timeProvider)
        : IRequestHandler<GetFlightDataUsageQuery, FlightDataUsageResult>
    {
        public async Task<FlightDataUsageResult> Handle(
            GetFlightDataUsageQuery request, CancellationToken ct)
        {
            var now = timeProvider.GetUtcNow();
            var (startUtc, endUtc) = UsageRange.Resolve(
                new GetAeroApiUsageQuery(request.StartUtc, request.EndUtc), now, AeroApiUsageCache.Duration);

            // The same cache entry the gauge and the budget guard read, so this report costs no
            // metered call of its own -- /account/usage is billed at $0.00 anyway, but it does take
            // a throttle slot, which is the thing worth not spending twice.
            var (usage, _) = await AeroApiUsageCache.GetOrFetchAsync(
                usageProvider, cache, startUtc, endUtc, ct);

            var nowUtc = now.UtcDateTime;
            var activeTrackedFlights = await flights.CountActiveTrackedFlightsAsync(
                nowUtc, syncOptions.StopAfterArrival, syncOptions.StopWithoutArrivalAfter, ct);
            var byMode = await flights.CountTrackedFlightsByStoredSyncModeAsync(ct);
            var yield = await flights.CountStatusEventsBySourceAsync(
                startUtc.UtcDateTime, endUtc.UtcDateTime, ct);

            var totalEvents = yield.Sum(y => y.Events);

            return new FlightDataUsageResult(
                RangeStartUtc: startUtc,
                RangeEndUtc: endUtc,
                TotalCalls: usage.TotalCalls,
                TotalPages: usage.TotalPages,
                TotalCostUsd: usage.TotalCostUsd,
                MonthlyBudgetUsd: budgetOptions.MonthlyBudgetUsd,
                ByOperation: usage.ByOperation
                    .Select(o => new FlightDataSpendOperationResult(o.Operation, o.Calls, o.Pages, o.CostUsd))
                    .ToList(),
                ActiveTrackedFlights: activeTrackedFlights,
                // Ordered by the enum's own declaration order (Fast, Live, Schedule, Stopped) with
                // the never-planned group last, so the panel renders a stable sequence rather than
                // whatever order PostgreSQL's GROUP BY happened to return.
                ByStoredSyncMode: byMode
                    .OrderBy(r => r.Mode is null ? int.MaxValue : (int)r.Mode.Value)
                    .Select(r => new FlightDataSyncModeCountResult(r.Mode?.ToString(), r.Count))
                    .ToList(),
                TotalEventsRecorded: totalEvents,
                EventYield: yield
                    .OrderByDescending(y => y.Events)
                    .ThenBy(y => y.Source, StringComparer.Ordinal)
                    .Select(y => new FlightDataEventYieldResult(y.Source, y.Events, y.DistinctFlights))
                    .ToList(),
                // Null rather than zero when the denominator is zero: "no tracked flights" is not
                // "$0 per flight", and a panel dividing by it would render a meaningless infinity.
                Derived: new FlightDataDerivedResult(
                    CostUsdPerActiveTrackedFlight: activeTrackedFlights == 0
                        ? null
                        : usage.TotalCostUsd / activeTrackedFlights,
                    CostUsdPerRecordedEvent: totalEvents == 0
                        ? null
                        : usage.TotalCostUsd / totalEvents));
        }
    }
}
