using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.AviationStack
{
    /// <summary>
    /// Last-resort source for an airport's IANA timezone, from AviationStack's own airport lookup.
    ///
    /// Endpoint: GET /v1/airports?access_key={key}&amp;iata_code={iata}
    ///
    /// Our reference data carries a <c>Timezone</c> per airport, but it is not guaranteed to be
    /// populated — and when it is missing, timetable rows cannot be converted to UTC and would
    /// otherwise be dropped. Asking the provider keeps search working regardless of the state of
    /// our own reference data.
    ///
    /// Failures here are never fatal: this is a fallback, so an unreachable or unhelpful response
    /// returns null and lets the caller decide.
    /// </summary>
    public sealed class AviationStackAirportTimezoneLookup
    {
        private readonly HttpClient _http;
        private readonly ILogger<AviationStackAirportTimezoneLookup> _logger;
        private readonly string _apiKey;

        public AviationStackAirportTimezoneLookup(
            HttpClient http,
            IConfiguration config,
            ILogger<AviationStackAirportTimezoneLookup> logger)
        {
            _http = http;
            _logger = logger;
            _apiKey = config["AviationStack:ApiKey"] ?? string.Empty;
        }

        public async Task<string?> TryGetTimezoneAsync(string iata, CancellationToken ct = default)
        {
            try
            {
                var url = $"airports?access_key={Uri.EscapeDataString(_apiKey)}" +
                          $"&iata_code={Uri.EscapeDataString(iata)}";

                var response = await _http.GetAsync(url, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array ||
                    data.GetArrayLength() == 0)
                    return null;

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
