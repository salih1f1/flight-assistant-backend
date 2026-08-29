using System.Text.Json;
using FlightAssistant.Infrastructure.ExternalServices.AviationStack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.FlightSearch
{
    /// <summary>
    /// Resolves an IATA flight code to the airport it departs from, via AviationStack's airline
    /// routes lookup.
    ///
    /// Endpoint: GET /v1/routes?access_key={key}&amp;airline_iata={carrier}&amp;flight_number={digits}
    ///
    /// This exists solely because the schedules endpoint requires an airport: a search by flight
    /// number alone on a future date cannot be served without first learning where the flight
    /// starts. The route lookup's own times are coarser than the schedules endpoint's
    /// (10:00 versus an actual 09:35), so only the airport code and timezone are taken from it.
    /// </summary>
    public sealed class AviationStackRouteResolver
    {
        private const string Operation = "Flight route lookup";

        private readonly HttpClient _http;
        private readonly ILogger<AviationStackRouteResolver> _logger;
        private readonly string _apiKey;

        public AviationStackRouteResolver(
            HttpClient http,
            IConfiguration config,
            ILogger<AviationStackRouteResolver> logger)
        {
            _http = http;
            _logger = logger;
            _apiKey = config["AviationStack:ApiKey"] ?? string.Empty;
        }

        /// <summary>The departure airport IATA code and its IANA timezone, when resolvable.</summary>
        public sealed record ResolvedRoute(string DepartureIata, string? Timezone);

        /// <summary>
        /// Returns the departure airport for <paramref name="flightNumber"/> (e.g. "TK1028"), or
        /// null when the flight code is malformed or the provider knows no such route. A null
        /// result is a legitimate "not findable", not a failure.
        /// </summary>
        public async Task<ResolvedRoute?> ResolveDepartureAsync(string flightNumber, CancellationToken ct = default)
        {
            var split = SplitFlightNumber(flightNumber);
            if (split is null) return null;

            var (airlineIata, digits) = split.Value;
            var url = $"routes?access_key={Uri.EscapeDataString(_apiKey)}" +
                      $"&airline_iata={Uri.EscapeDataString(airlineIata)}" +
                      $"&flight_number={Uri.EscapeDataString(digits)}";

            var response = await _http.GetAsync(url, ct);

            using var doc = await AviationStackResponse.ReadOrThrowAsync(
                response, Operation, flightNumber, _logger, ct);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
            {
                _logger.LogInformation(
                    "AviationStack knows no route for {FlightNumber}; cannot resolve a departure airport",
                    flightNumber);
                return null;
            }

            var first = data[0];
            if (!first.TryGetProperty("departure", out var dep) || dep.ValueKind != JsonValueKind.Object)
                return null;

            var iata = TryGetString(dep, "iata");
            if (string.IsNullOrWhiteSpace(iata)) return null;

            return new ResolvedRoute(iata.ToUpperInvariant(), TryGetString(dep, "timezone"));
        }

        /// <summary>
        /// Splits "TK1028" into ("TK", "1028"). The validator already enforces this shape, so a
        /// null here means the caller bypassed it.
        /// </summary>
        private static (string AirlineIata, string Digits)? SplitFlightNumber(string flightNumber)
        {
            if (string.IsNullOrWhiteSpace(flightNumber) || flightNumber.Length < 3) return null;

            var carrier = flightNumber[..2];
            var digits = flightNumber[2..];

            return digits.All(char.IsAsciiDigit)
                ? (carrier.ToUpperInvariant(), digits.TrimStart('0') is { Length: > 0 } t ? t : digits)
                : null;
        }

        private static string? TryGetString(JsonElement el, string field)
        {
            if (el.ValueKind == JsonValueKind.Undefined) return null;
            if (el.TryGetProperty(field, out var val) && val.ValueKind == JsonValueKind.String)
                return val.GetString();
            return null;
        }
    }
}
