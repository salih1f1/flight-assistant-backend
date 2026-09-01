using System.Globalization;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
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
    /// current UTC month to date — see <see cref="UsageRange.Resolve"/>.
    /// </summary>
    public sealed record GetAeroApiUsageQuery(
        DateTimeOffset? StartUtc = null,
        DateTimeOffset? EndUtc = null) : IRequest<AeroApiUsageResult>;

    /// <summary>
    /// The one copy of "what does an omitted bound default to", owned by neither the validator nor
    /// the handler — a validator runs before a handler and must not depend on one, which is exactly
    /// what having the validator call a handler static amounted to. The validator only needs
    /// <see cref="DefaultStartUtc"/> to phrase a 400; the handler needs the full <see cref="Resolve"/>,
    /// which also floors the end onto the cache grid and is the backstop that guarantees the range
    /// it ships upstream is never inverted, regardless of what the validator's own — separately
    /// sampled — clock allowed through.
    /// </summary>
    internal static class UsageRange
    {
        /// <summary>An omitted start means the first instant of the current UTC month — this is a
        /// monthly budget gauge.</summary>
        public static DateTimeOffset DefaultStartUtc(DateTimeOffset now)
        {
            var nowUtc = now.ToUniversalTime();
            return new DateTimeOffset(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        }

        /// <summary>
        /// Resolves the range actually reported on, requested upstream, and used as the cache key —
        /// deliberately one triple, so the response can never claim a window the numbers did not
        /// come from.
        ///
        /// <para>
        /// An omitted start defaults per <see cref="DefaultStartUtc"/>. An omitted end means
        /// <i>now, floored onto <paramref name="grid"/></i> — the flooring is what makes the cache
        /// work at all (an unrounded "now" would produce a distinct key on every request, so the
        /// handler would spend a metered call and a throttle slot on every dashboard refresh) and it
        /// keeps the key changing exactly when upstream refreshes. Where the floor would land on or
        /// before the resolved start — the first minutes of a month, or a start given inside the
        /// current bucket — flooring would invert the window, so raw "now" is used there instead;
        /// the window may then be zero-width (never inverted, but may be empty) and the only cost is
        /// no caching for those few minutes.
        /// </para>
        ///
        /// <para>
        /// An <b>explicit</b> end gets the same treatment once it reaches or passes the floored
        /// "now" boundary — whether because it names an instant still in the future (never sent
        /// upstream unverified — the exact behaviour the default path was shaped to avoid) or
        /// because it falls inside the bucket "now" is already in (the natural shape of a
        /// range-picker UI asking for "up to now", which must land on the same key as every other
        /// request in that bucket or the cache would never hit). An explicit end from an earlier
        /// bucket is used exactly as given.
        /// </para>
        ///
        /// <para>
        /// Both bounds are truncated to whole seconds: the cache key is formatted to second
        /// precision, so a resolved range that kept sub-second ticks — as raw "now" does — could
        /// share a cache key with a request whose reported end differs from it by a fraction of a
        /// second.
        /// </para>
        /// </summary>
        public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) Resolve(
            GetAeroApiUsageQuery request, DateTimeOffset now, TimeSpan grid)
        {
            var nowUtc = TruncateToSeconds(now.ToUniversalTime());

            var startUtc = request.StartUtc.HasValue
                ? TruncateToSeconds(request.StartUtc.Value.ToUniversalTime())
                : DefaultStartUtc(nowUtc);

            var flooredNow = new DateTimeOffset(
                nowUtc.Ticks - nowUtc.Ticks % grid.Ticks, TimeSpan.Zero);
            var effectiveNow = flooredNow > startUtc ? flooredNow : nowUtc;

            DateTimeOffset endUtc;
            if (request.EndUtc is { } requestedEnd)
            {
                var candidateEnd = TruncateToSeconds(requestedEnd.ToUniversalTime());
                endUtc = candidateEnd >= flooredNow ? effectiveNow : candidateEnd;
            }
            else
            {
                endUtc = effectiveNow;
            }

            // The backstop: enforced here, not just at the API edge, because this is the one place
            // that cannot be bypassed by a caller — the validator included — that sampled the clock
            // at a different instant than this call did.
            if (endUtc < startUtc)
                throw new DomainException(
                    "The resolved usage range was inverted. Please retry.", 400);

            return (startUtc, endUtc);
        }

        private static DateTimeOffset TruncateToSeconds(DateTimeOffset value) =>
            new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

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

            // A usage report of the future is meaningless, and — with no end supplied — it would
            // also resolve to an inverted range, which upstream would answer with either a 400 or a
            // confidently empty report.
            RuleFor(x => x.StartUtc)
                .Must(start => start!.Value.ToUniversalTime() <= timeProvider.GetUtcNow())
                .When(x => x.StartUtc.HasValue)
                .WithMessage("The start of the range cannot be in the future.");

            // Mirrors the start rule. Without it, an explicit future end (the natural shape of a
            // range-picker UI's "to" field before the user finishes picking) would either be
            // forwarded upstream unverified or silently clamped by UsageRange.Resolve with no 400 to
            // explain why the reported end does not match what was asked for.
            RuleFor(x => x.EndUtc)
                .Must(end => end!.Value.ToUniversalTime() <= timeProvider.GetUtcNow())
                .When(x => x.EndUtc.HasValue)
                .WithMessage("The end of the range cannot be in the future.");

            // Compared against the *defaulted* start, not just an explicitly supplied one: an
            // end-only request (say ?end=2026-08-01 in September) would otherwise sail past this
            // rule and invert the defaulted month range. This only needs the default-start rule, not
            // the handler's full resolution, so it depends on UsageRange — never on the handler —
            // and UsageRange.Resolve itself is the backstop if this sampled a different instant of
            // "now" than the handler ultimately resolves against.
            RuleFor(x => x.EndUtc)
                .Must((query, end) =>
                {
                    var startUtc = query.StartUtc?.ToUniversalTime()
                        ?? UsageRange.DefaultStartUtc(timeProvider.GetUtcNow());
                    return end!.Value.ToUniversalTime() > startUtc;
                })
                .When(x => x.EndUtc.HasValue)
                .WithMessage(
                    "The end of the range must be after its start — with no start given, that is " +
                    "the first of the current UTC month.");
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
            var (startUtc, endUtc) = UsageRange.Resolve(request, _timeProvider.GetUtcNow(), CacheDuration);
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

        private static string CacheKey(DateTimeOffset startUtc, DateTimeOffset endUtc) =>
            CacheKeyPrefix
            + startUtc.UtcDateTime.ToString(RangeFormat, CultureInfo.InvariantCulture)
            + ".."
            + endUtc.UtcDateTime.ToString(RangeFormat, CultureInfo.InvariantCulture);
    }
}
