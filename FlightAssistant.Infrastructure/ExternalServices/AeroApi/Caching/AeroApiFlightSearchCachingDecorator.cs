using System.Globalization;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi.Caching
{
    /// <summary>
    /// Caches <see cref="IFlightSearchProvider.SearchAsync"/> results under <see cref="ICacheService"/>
    /// so that <c>GET /flights/search</c> — anonymous, rate-limited only per IP — cannot drain the
    /// metered AeroAPI personal tier ($5/month, billed per page fetched). A repeated identical search
    /// issues zero upstream calls; see issue #282.
    ///
    /// <para>
    /// <b>TTL follows <see cref="FlightSearchCriteria.Date"/>.</b> A date that is today or in the past
    /// gets the short <see cref="AeroApiOptions.SearchCacheTtlPastOrToday"/> TTL (default ~5 minutes) —
    /// actual/estimated times can still change for a service date that has started or already
    /// happened. A future date gets the long <see cref="AeroApiOptions.SearchCacheTtlFuture"/> TTL
    /// (default ~12 hours), because a published future schedule barely moves; this is the TTL that
    /// actually protects the budget, since most searches — and every scripted repeat worth worrying
    /// about — target future dates. "Today" is read from the injected <see cref="TimeProvider"/>
    /// (never <c>DateTime.UtcNow</c>) so the boundary is deterministic under test. The comparison uses
    /// the UTC calendar date rather than resolving the search's own origin-local day: the TTL split is
    /// a cost control, not something that needs the same precision as the provider's own local-day
    /// window shift.
    /// </para>
    ///
    /// <para>
    /// <b>An empty result is cached like any other success.</b> <see cref="IFlightSearchProvider.SearchAsync"/>
    /// returns an empty list (not null) when the provider responds successfully but nothing matches —
    /// that is a valid answer, not a miss. Not caching it would leave the single cheapest attack
    /// available: repeatedly searching a route with no flights would hit upstream on every request.
    /// </para>
    ///
    /// <para>
    /// <b>A failure is never cached.</b> <see cref="IFlightSearchProvider.SearchAsync"/> throws a
    /// <see cref="FlightAssistant.Domain.Common.Exceptions.DomainException"/> when the provider is
    /// unreachable or errors — deliberately distinct from a successful empty result — and this
    /// decorator lets it propagate before it ever reaches the caching step, so a transient upstream
    /// outage cannot get "stuck" cached for the TTL and the very next call retries upstream.
    /// </para>
    ///
    /// <para>
    /// <b>The key carries the provider (#531).</b> Before this, the key was
    /// <c>dep|arr|date|fn|cs</c> with no provider dimension, so a route-search key could be served
    /// the other provider's rows for up to <see cref="AeroApiOptions.SearchCacheTtlFuture"/> after
    /// routing changed — tolerable when that took a deploy, not when an operator can flip it from
    /// the admin panel. The provider comes from <see cref="IFlightSearchRouter"/>, the same single
    /// owner the selector reads, and this retires the "flush the <c>aeroapi:flight-search:</c>
    /// prefix on rollback" step that used to be required. The cost is one extra cache miss per key
    /// per provider after a flip.
    /// </para>
    /// </summary>
    public sealed class AeroApiFlightSearchCachingDecorator : IFlightSearchProvider
    {
        private const string CacheKeyPrefix = "aeroapi:flight-search:";

        private readonly IFlightSearchProvider _inner;
        private readonly IFlightSearchRouter _router;
        private readonly ICacheService _cache;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _ttlPastOrToday;
        private readonly TimeSpan _ttlFuture;
        private readonly ILogger<AeroApiFlightSearchCachingDecorator> _logger;

        public AeroApiFlightSearchCachingDecorator(
            IFlightSearchProvider inner,
            IFlightSearchRouter router,
            ICacheService cache,
            IOptions<AeroApiOptions> options,
            TimeProvider timeProvider,
            ILogger<AeroApiFlightSearchCachingDecorator> logger)
        {
            _inner = inner;
            _router = router;
            _cache = cache;
            _timeProvider = timeProvider;
            _ttlPastOrToday = options.Value.SearchCacheTtlPastOrToday;
            _ttlFuture = options.Value.SearchCacheTtlFuture;
            _logger = logger;
        }

        public async Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default)
        {
            // Which provider will serve this search is part of the cache identity. The router is the
            // single owner of that rule (#531) — re-deriving it here would be a second copy free to
            // drift from the selector's.
            var provider = await _router.ResolveAsync(criteria, ct);
            var key = BuildCacheKey(criteria, provider);

            var cached = await _cache.GetAsync<List<FlightSearchResult>>(key, ct);
            if (cached is not null)
            {
                _logger.LogDebug("Flight search cache hit for {CacheKey}", key);
                return cached;
            }

            // Not caught here: a DomainException from _inner propagates straight to the caller,
            // never reaching the SetAsync below — see class doc, "a failure is never cached".
            var results = await _inner.SearchAsync(criteria, ct);

            var ttl = criteria.Date <= Today() ? _ttlPastOrToday : _ttlFuture;

            // Cached whether or not it's empty — see class doc, "an empty result is cached like any
            // other success". Stored as a concrete List<T> (rather than the IReadOnlyList<T> return
            // type) because System.Text.Json — which RedisCacheService uses under ICacheService —
            // cannot deserialize straight into an interface.
            await _cache.SetAsync(key, results.ToList(), ttl, ct);

            return results;
        }

        private DateOnly Today() => DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

        private static string BuildCacheKey(FlightSearchCriteria criteria, string provider) =>
            $"{CacheKeyPrefix}provider={provider}" +
            $"|dep={Normalize(criteria.DepartureIata)}" +
            $"|arr={Normalize(criteria.ArrivalIata)}" +
            $"|date={criteria.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
            $"|fn={Normalize(criteria.FlightNumber)}" +
            $"|cs={criteria.IncludeCodeshares}";

        /// <summary>
        /// Normalises one criteria field for the cache key: uppercased and trimmed so a
        /// case/whitespace-only difference shares one entry. A null field and a present-but-empty
        /// field are kept distinct via an explicit presence flag ("N" vs "V" + value) rather than by
        /// folding a null into "" — otherwise <c>(DepartureIata: null)</c> and
        /// <c>(DepartureIata: "")</c> would silently share a cache entry.
        /// </summary>
        private static string Normalize(string? value) =>
            value is null ? "N" : "V" + value.Trim().ToUpperInvariant();
    }
}
