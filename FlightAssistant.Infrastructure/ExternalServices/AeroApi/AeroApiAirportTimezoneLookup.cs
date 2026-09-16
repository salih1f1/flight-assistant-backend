using System.Net;
using System.Text.Json;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Resolves an airport's IANA timezone from FlightAware AeroAPI.
    ///
    /// Endpoint: GET /airports/{id}
    ///
    /// A success is cached under <c>ICacheService</c> for 365 days, because airport timezone
    /// assignments change on the order of once a decade. An airport AeroAPI doesn't know, or has no
    /// timezone for, is kept as a miss for 1 hour. A transient failure isn't cached: a cached miss
    /// sends searches from that origin to the UTC-date fallback, which gives wrong-day rows for a
    /// far-offset airport, and the search cache would keep them. The per-search dedupe already caps
    /// a transient failure at one retried lookup per origin per search (#373).
    ///
    /// AeroAPI's <c>scheduled_out</c>/<c>scheduled_in</c> timestamps are already UTC — this
    /// lookup exists purely to resolve the *origin's local day* for
    /// <see cref="AeroApiFlightSearchProvider"/>'s UTC window shift, not to reinterpret a
    /// timezone-naive timestamp.
    ///
    /// Failures are never fatal: this returns null and lets the caller fall back. A timezone
    /// lookup is supporting infrastructure for a search, not a user-facing operation in its own
    /// right, so it must never surface a <see cref="DomainException"/> to a search caller.
    /// </summary>
    public sealed class AeroApiAirportTimezoneLookup
    {
        // Airport time zones change about once a decade, so a success is kept for a year (#373).
        // Internal (not private) so AeroApiVerificationSource (#401, R7) can write under the exact
        // same TTL when it opportunistically fills this cache from an already-paid-for airport
        // check -- two different durations for the same key would just mean whichever wrote last wins.
        internal static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromDays(365);

        // An airport AeroAPI doesn't know (404, 400) or has no timezone for is kept as a miss for an
        // hour (#373). A transient failure isn't cached at all: a cached miss would send that origin's
        // searches to the UTC-date fallback, wrong-day rows for a far-offset airport that the search
        // cache would then keep.
        private static readonly TimeSpan MissCacheDuration = TimeSpan.FromHours(1);

        // A cached miss is the empty string: GetAsync returns null for an absent key but "" for this
        // marker, in both MemoryCacheService and RedisCacheService (JSON "" round-trips). Only an
        // airport AeroAPI doesn't know writes it, never a transient failure. Zone ids cached before
        // #373 are non-empty strings, so they stay readable.
        private const string CachedMiss = "";

        private const string Operation = "Airport lookup";
        private const string CacheKeyPrefix = "aeroapi:airport-tz:";

        /// <summary>
        /// The timezone cache key for one IATA code, upper-cased. A small shared helper (#401, R7)
        /// rather than exposing <see cref="CacheKeyPrefix"/> alone: <see cref="AeroApiVerificationSource"/>
        /// fills this same cache from an airport check, and a caller that forgot to upper-case first
        /// would write a second, differently-cased entry nothing ever reads back -- one method is the
        /// one place that can't happen.
        /// </summary>
        internal static string CacheKeyFor(string iata) => CacheKeyPrefix + iata.ToUpperInvariant();

        private readonly HttpClient _http;
        private readonly ICacheService _cache;
        private readonly string _apiKey;
        private readonly ILogger<AeroApiAirportTimezoneLookup> _logger;

        public AeroApiAirportTimezoneLookup(
            HttpClient http,
            ICacheService cache,
            IOptions<AeroApiOptions> options,
            ILogger<AeroApiAirportTimezoneLookup> logger)
        {
            _http = http;
            _cache = cache;
            _apiKey = options.Value.ApiKey;
            _logger = logger;
        }

        public async Task<string?> TryGetTimezoneAsync(string iata, CancellationToken ct = default)
        {
            var normalized = iata.ToUpperInvariant();
            var key = CacheKeyFor(normalized);

            var cached = await _cache.GetAsync<string>(key, ct);
            if (cached is not null)
                return cached.Length == 0 ? null : cached;

            var result = await FetchAsync(normalized, ct);

            if (result.Timezone is not null)
            {
                await _cache.SetAsync(key, result.Timezone, SuccessCacheDuration, ct);
                return result.Timezone;
            }

            if (result.IsPermanentMiss)
                await _cache.SetAsync(key, CachedMiss, MissCacheDuration, ct);

            return null;
        }

        /// <summary>A usable timezone, or no timezone plus whether the miss is AeroAPI's answer for
        /// this airport (cached) rather than a transient failure (not cached).</summary>
        private readonly record struct FetchResult(string? Timezone, bool IsPermanentMiss)
        {
            public static FetchResult Found(string timezone) => new(timezone, IsPermanentMiss: false);

            public static FetchResult PermanentMiss => new(null, IsPermanentMiss: true);

            public static FetchResult TransientFailure => new(null, IsPermanentMiss: false);
        }

        private async Task<FetchResult> FetchAsync(string iata, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync($"airports/{Uri.EscapeDataString(iata)}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not reach AeroAPI to look up a timezone for airport {Iata}", iata);
                return FetchResult.TransientFailure;
            }

            // Captured before ReadOrThrowAsync, which maps a 404 and a 5xx to the same 502.
            var status = response.StatusCode;

            try
            {
                using var doc = await AeroApiResponse.ReadOrThrowAsync(
                    response, Operation, iata, _apiKey, _logger, ct);

                var timezone = doc.RootElement.TryGetProperty("timezone", out var tz) && tz.ValueKind == JsonValueKind.String
                    ? tz.GetString()
                    : null;

                // A 200 with no usable timezone is AeroAPI's answer for this airport, not a failed call.
                return string.IsNullOrWhiteSpace(timezone)
                    ? FetchResult.PermanentMiss
                    : FetchResult.Found(timezone);
            }
            catch (DomainException ex)
            {
                // Swallow rather than propagate: the caller (AeroApiFlightSearchProvider) has its
                // own degraded-but-correct fallback for "no zone available" and must not have an
                // entire search fail just because one airport's lookup was throttled or unknown.
                _logger.LogWarning(ex, "Airport timezone lookup for {Iata} failed: {Message}", iata, ex.Message);

                // An unknown airport (404) or a code AeroAPI rejects (400) won't change within the hour;
                // anything else (429, 401/403, 5xx, an unparseable 200) may clear on the next search.
                return status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest
                    ? FetchResult.PermanentMiss
                    : FetchResult.TransientFailure;
            }
        }
    }
}
