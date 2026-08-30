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
    /// Results are cached under <c>ICacheService</c> for 30 days, successes only, because airport
    /// timezone assignments change on the order of once a decade.
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
        private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(30);
        private const string Operation = "Airport lookup";
        private const string CacheKeyPrefix = "aeroapi:airport-tz:";

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
            var key = CacheKeyPrefix + normalized;

            var cached = await _cache.GetAsync<string>(key, ct);
            if (!string.IsNullOrWhiteSpace(cached)) return cached;

            var timezone = await FetchAsync(normalized, ct);

            // Only successes are cached — caching a miss would keep an airport "broken" for 30
            // days over what may have been a single throttled or transient failure.
            if (!string.IsNullOrWhiteSpace(timezone))
                await _cache.SetAsync(key, timezone, CacheDuration, ct);

            return timezone;
        }

        private async Task<string?> FetchAsync(string iata, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync($"airports/{Uri.EscapeDataString(iata)}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not reach AeroAPI to look up a timezone for airport {Iata}", iata);
                return null;
            }

            try
            {
                using var doc = await AeroApiResponse.ReadOrThrowAsync(
                    response, Operation, iata, _apiKey, _logger, ct);

                return doc.RootElement.TryGetProperty("timezone", out var tz) && tz.ValueKind == JsonValueKind.String
                    ? tz.GetString()
                    : null;
            }
            catch (DomainException ex)
            {
                // Swallow rather than propagate: the caller (AeroApiFlightSearchProvider) has its
                // own degraded-but-correct fallback for "no zone available" and must not have an
                // entire search fail just because one airport's lookup was throttled or unknown.
                _logger.LogWarning(ex, "Airport timezone lookup for {Iata} failed: {Message}", iata, ex.Message);
                return null;
            }
        }
    }
}
