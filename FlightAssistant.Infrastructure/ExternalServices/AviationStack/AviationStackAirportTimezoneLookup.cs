using System.Text.Json;
using FlightAssistant.Application.Interfaces.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.AviationStack
{
    /// <summary>
    /// Resolves an airport's IANA timezone from AviationStack's airport lookup.
    ///
    /// Endpoint: GET /v1/airports?access_key={key}&amp;iata_code={iata}
    ///
    /// This is the authority for interpreting AviationStack timestamps, deliberately in preference
    /// to our own <c>Airport.Timezone</c> reference data: that column is imported by hand, so it
    /// can be absent, stale, or hold the wrong thing entirely — OpenFlights ships a numeric GMT
    /// offset column right beside the IANA one, and importing that yields a value that is present
    /// but useless. The times being converted come from AviationStack, so the zone that interprets
    /// them should too.
    ///
    /// Results are cached (airport timezones effectively never change) so this costs at most one
    /// call per airport per cache window, which matters on a rate-limited plan.
    ///
    /// Failures are never fatal: this returns null and lets the caller decide.
    /// </summary>
    public sealed class AviationStackAirportTimezoneLookup
    {
        /// <summary>
        /// Airport timezone assignments change on the order of once a decade, per country, by
        /// legislation. A month is comfortably shorter than that and keeps quota use negligible.
        /// </summary>
        private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(30);

        private readonly HttpClient _http;
        private readonly ICacheService _cache;
        private readonly ILogger<AviationStackAirportTimezoneLookup> _logger;
        private readonly string _apiKey;

        public AviationStackAirportTimezoneLookup(
            HttpClient http,
            ICacheService cache,
            IConfiguration config,
            ILogger<AviationStackAirportTimezoneLookup> logger)
        {
            _http = http;
            _cache = cache;
            _logger = logger;
            _apiKey = config["AviationStack:ApiKey"] ?? string.Empty;
        }

        public async Task<string?> TryGetTimezoneAsync(string iata, CancellationToken ct = default)
        {
            var key = $"airport-tz:{iata.ToUpperInvariant()}";

            var cached = await _cache.GetAsync<string>(key, ct);
            if (!string.IsNullOrWhiteSpace(cached)) return cached;

            var timezone = await FetchAsync(iata, ct);

            // Only successes are cached. Caching a miss would keep an airport broken for a month
            // over what may have been a single throttled request.
            if (!string.IsNullOrWhiteSpace(timezone))
                await _cache.SetAsync(key, timezone, CacheDuration, ct);

            return timezone;
        }

        private async Task<string?> FetchAsync(string iata, CancellationToken ct)
        {
            try
            {
                var url = $"airports?access_key={Uri.EscapeDataString(_apiKey)}" +
                          $"&iata_code={Uri.EscapeDataString(iata)}";

                var response = await _http.GetAsync(url, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(json);

                // Report why we came back empty. Silently returning null would leave the caller
                // unable to tell a throttled lookup from an unknown airport.
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    _logger.LogWarning(
                        "Airport timezone lookup for {Iata} failed (HTTP {StatusCode}): {Error}",
                        iata, (int)response.StatusCode, error.ToString());
                    return null;
                }

                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array ||
                    data.GetArrayLength() == 0)
                {
                    _logger.LogWarning(
                        "Airport timezone lookup for {Iata} returned no airport (HTTP {StatusCode})",
                        iata, (int)response.StatusCode);
                    return null;
                }

                return data[0].TryGetProperty("timezone", out var tz) && tz.ValueKind == JsonValueKind.String
                    ? tz.GetString()
                    : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A fallback that throws would be worse than one that returns nothing.
                _logger.LogWarning(ex, "Could not look up a timezone for airport {Iata}", iata);
                return null;
            }
        }
    }
}
