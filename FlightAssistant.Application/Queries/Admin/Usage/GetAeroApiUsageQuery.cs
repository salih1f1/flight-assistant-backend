using System.Globalization;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Usage
{
    /// <summary>One operation's slice of the reported spend, straight from the provider.</summary>
    public sealed record AeroApiUsageOperationResult(
        string Operation,
        int Calls,
        int Pages,
        decimal CostUsd,
        int SuccessfulCalls,
        int FailedCalls);

    /// <summary>
    /// AeroAPI spend for the resolved range, plus the two pieces of local context the panel needs
    /// to render it: the configured <see cref="MonthlyBudgetUsd"/> the gauge is drawn against, and
    /// the throttle's <see cref="NextSlotUtc"/>.
    /// </summary>
    /// <param name="NextSlotUtc">Live throttle state, deliberately <b>not</b> part of the cached
    /// upstream payload — it is read fresh on every request, so a cache hit never serves a stale
    /// "next slot" instant.</param>
    public sealed record AeroApiUsageResult(
        DateTimeOffset RangeStartUtc,
        DateTimeOffset RangeEndUtc,
        int TotalCalls,
        int TotalPages,
        decimal TotalCostUsd,
        decimal TotalDiscountCostUsd,
        int TotalSuccessfulCalls,
        int TotalFailedCalls,
        decimal MonthlyBudgetUsd,
        IReadOnlyList<AeroApiUsageOperationResult> ByOperation,
        DateTimeOffset? NextSlotUtc);

    /// <summary>
    /// Reads AeroAPI's own account usage report. Both bounds are optional and default to the
    /// current UTC month to date — see <see cref="GetAeroApiUsageQueryHandler.ResolveRange"/>.
    /// </summary>
    public sealed record GetAeroApiUsageQuery(
        DateTimeOffset? StartUtc = null,
        DateTimeOffset? EndUtc = null) : IRequest<AeroApiUsageResult>;

    public sealed class GetAeroApiUsageQueryValidator : AbstractValidator<GetAeroApiUsageQuery>
    {
        /// <summary>AeroAPI serves at most one year of usage history.</summary>
        public const int MaxHistoryYears = 1;

        public GetAeroApiUsageQueryValidator(TimeProvider timeProvider)
        {
            // Bounce an out-of-range start here rather than letting AeroAPI reject it: a 400 that
            // names the real limit is worth more to the caller than one relayed from upstream, and
            // it costs no metered call to produce.
            RuleFor(x => x.StartUtc)
                .Must(start => start!.Value.ToUniversalTime()
                               >= timeProvider.GetUtcNow().AddYears(-MaxHistoryYears))
                .When(x => x.StartUtc.HasValue)
                .WithMessage(
                    $"AeroAPI caps usage history at {MaxHistoryYears} year into the past — " +
                    "choose a later start.");

            RuleFor(x => x.EndUtc)
                .Must((query, end) => end!.Value.ToUniversalTime() > query.StartUtc!.Value.ToUniversalTime())
                .When(x => x.StartUtc.HasValue && x.EndUtc.HasValue)
                .WithMessage("The end of the range must be after its start.");
        }
    }

    /// <summary>
    /// Proxies <c>GET /account/usage</c>, cached for <see cref="CacheDuration"/> under the resolved
    /// range.
    ///
    /// <para>
    /// <b>Why upstream and not a local meter.</b> FlightAware bills per <i>page</i>, not per call,
    /// and bills failed calls too — a locally counted, locally priced model gets both wrong and
    /// cannot be audited against the invoice. The provider's own report is authoritative, and the
    /// endpoint serving it is itself billed at $0.00, so proxying it costs nothing but a throttle
    /// slot.
    /// </para>
    ///
    /// <para>
    /// <b>Why 10 minutes.</b> FlightAware refreshes these figures every 10 minutes, so a shorter
    /// TTL would buy no freshness and would just spend throttle slots that flight search and status
    /// sync are queueing for.
    /// </para>
    /// </summary>
    public sealed class GetAeroApiUsageQueryHandler : IRequestHandler<GetAeroApiUsageQuery, AeroApiUsageResult>
    {
        /// <summary>Matches FlightAware's own refresh cadence for this data.</summary>
        public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

        private const string CacheKeyPrefix = "aeroapi:account-usage:";
        private const string RangeFormat = "yyyy-MM-ddTHH:mm:ssZ";

        private readonly IAeroApiAccountUsageProvider _usage;
        private readonly ICacheService _cache;
        private readonly IAeroApiBudgetOptions _budget;
        private readonly IAeroApiThrottleObserver _throttle;
        private readonly TimeProvider _timeProvider;

        public GetAeroApiUsageQueryHandler(
            IAeroApiAccountUsageProvider usage,
            ICacheService cache,
            IAeroApiBudgetOptions budget,
            IAeroApiThrottleObserver throttle,
            TimeProvider timeProvider)
        {
            _usage = usage;
            _cache = cache;
            _budget = budget;
            _throttle = throttle;
            _timeProvider = timeProvider;
        }

        public async Task<AeroApiUsageResult> Handle(GetAeroApiUsageQuery request, CancellationToken ct)
        {
            var (startUtc, endUtc) = ResolveRange(request, _timeProvider.GetUtcNow());
            var key = CacheKey(startUtc, endUtc);

            // Only the upstream payload is cached — the budget and the throttle's next slot are
            // local state that must not be frozen for 10 minutes alongside it.
            var usage = await _cache.GetAsync<AeroApiAccountUsage>(key, ct);
            if (usage is null)
            {
                usage = await _usage.GetUsageAsync(startUtc, endUtc, ct);
                await _cache.SetAsync(key, usage, CacheDuration, ct);
            }

            return new AeroApiUsageResult(
                startUtc,
                endUtc,
                usage.TotalCalls,
                usage.TotalPages,
                usage.TotalCostUsd,
                usage.TotalDiscountCostUsd,
                usage.TotalSuccessfulCalls,
                usage.TotalFailedCalls,
                _budget.MonthlyBudgetUsd,
                usage.ByOperation
                    .Select(o => new AeroApiUsageOperationResult(
                        o.Operation, o.Calls, o.Pages, o.CostUsd, o.SuccessfulCalls, o.FailedCalls))
                    .ToList(),
                _throttle.NextSlotUtc);
        }

        /// <summary>
        /// Resolves the range actually reported on, requested upstream, and used as the cache key —
        /// deliberately one triple, so the response can never claim a window the numbers did not
        /// come from.
        ///
        /// <para>
        /// An omitted start means the first instant of the current UTC month: this is a monthly
        /// budget gauge. An omitted end means <i>now, floored onto the same 10-minute grid as
        /// <see cref="CacheDuration"/></i>. The flooring is what makes the cache work at all — an
        /// unrounded "now" would produce a distinct key on every request and the handler would call
        /// a metered API each time — and it keeps the key changing exactly when upstream refreshes.
        /// It also never sends a future timestamp upstream. In the first few minutes of a month the
        /// floor would land on (or before) the month start, which would ask for an empty window, so
        /// the raw "now" is used there instead; the only cost is no caching for those minutes.
        /// </para>
        /// </summary>
        public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) ResolveRange(
            GetAeroApiUsageQuery request, DateTimeOffset now)
        {
            var nowUtc = now.ToUniversalTime();

            var startUtc = request.StartUtc?.ToUniversalTime()
                ?? new DateTimeOffset(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);

            if (request.EndUtc is { } requestedEnd)
                return (startUtc, requestedEnd.ToUniversalTime());

            var flooredNow = new DateTimeOffset(
                nowUtc.Ticks - nowUtc.Ticks % CacheDuration.Ticks, TimeSpan.Zero);

            return (startUtc, flooredNow > startUtc ? flooredNow : nowUtc);
        }

        private static string CacheKey(DateTimeOffset startUtc, DateTimeOffset endUtc) =>
            CacheKeyPrefix
            + startUtc.UtcDateTime.ToString(RangeFormat, CultureInfo.InvariantCulture)
            + ".."
            + endUtc.UtcDateTime.ToString(RangeFormat, CultureInfo.InvariantCulture);
    }
}
