using System.Globalization;
using System.Text.Json;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.FlightSearch
{
    /// <summary>
    /// Searches for flights using the AviationStack real-time/historical flights endpoint.
    ///
    /// Endpoint: GET /v1/flights?access_key={key}&amp;dep_iata={dep}&amp;arr_iata={arr}&amp;flight_iata={no}&amp;dep_scheduled_time_dep={date}
    ///
    /// A search is anchored on a date plus either a full route (dep_iata + arr_iata) or a specific
    /// flight number (flight_iata); AviationStack filters server-side in a single request.
    ///
    /// AviationStack signals failures with an <c>error</c> object in the JSON body — sometimes even
    /// under an HTTP 200 — so the body is inspected regardless of status code and the
    /// <c>error.code</c> is mapped to an appropriate HTTP status (see <see cref="MapUpstreamError"/>).
    /// A successful response with no matching flights returns an empty list, which is distinct from
    /// a provider failure.
    /// </summary>
    public sealed class AviationStackFlightSearchProvider : IFlightSearchProvider
    {
        // 400 Bad Request — the provider rejected a parameter we sent (should be prevented by our
        // own validators, so this is surfaced for diagnostics).
        private const int StatusCode400BadRequest = 400;

        // 500 Internal Server Error — an AviationStack account/plan/config problem on our side
        // (bad key, plan restriction). The real cause is logged; the client sees a generic message.
        private const int StatusCode500InternalError = 500;

        // 502 Bad Gateway — the provider was unreachable or returned an unclassified error.
        private const int StatusCode502BadGateway = 502;

        // 503 Service Unavailable — the provider throttled us (rate/usage limit). The client may retry.
        private const int StatusCode503ServiceUnavailable = 503;

        private readonly HttpClient _http;
        private readonly ILogger<AviationStackFlightSearchProvider> _logger;
        private readonly string _apiKey;

        public AviationStackFlightSearchProvider(
            HttpClient http,
            IConfiguration config,
            ILogger<AviationStackFlightSearchProvider> logger)
        {
            _http = http;
            _logger = logger;
            _apiKey = config["AviationStack:ApiKey"] ?? string.Empty;
        }

        public async Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default)
        {
            var url = BuildUrl(criteria);

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AviationStack flight search request failed for {Criteria}", Describe(criteria));
                throw new DomainException(
                    "Flight search is temporarily unavailable: the flight data provider could not be reached. Please try again shortly.",
                    ex,
                    StatusCode502BadGateway);
            }

            var json = await response.Content.ReadAsStringAsync(ct);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex,
                    "AviationStack flight search returned unparseable response ({StatusCode}) for {Criteria}",
                    (int)response.StatusCode, Describe(criteria));
                throw new DomainException(
                    "Flight search is temporarily unavailable: the flight data provider returned an unexpected response. Please try again shortly.",
                    StatusCode502BadGateway);
            }

            using (doc)
            {
                // AviationStack reports errors via an `error` object in the body, sometimes under HTTP 200.
                if (doc.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object)
                {
                    throw MapUpstreamError(error, response, criteria);
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "AviationStack flight search returned {StatusCode} with no error body for {Criteria}",
                        (int)response.StatusCode, Describe(criteria));
                    throw new DomainException(
                        $"Flight search is temporarily unavailable: the flight data provider responded with status {(int)response.StatusCode}. Please try again shortly.",
                        StatusCode502BadGateway);
                }

                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    return [];

                var results = new List<FlightSearchResult>();
                foreach (var flight in data.EnumerateArray())
                {
                    var result = TryMap(flight, criteria);
                    if (result is not null)
                        results.Add(result);
                }

                return results;
            }
        }

        private string BuildUrl(FlightSearchCriteria criteria)
        {
            var query = new List<string> { $"access_key={Uri.EscapeDataString(_apiKey)}" };

            if (!string.IsNullOrWhiteSpace(criteria.DepartureIata))
                query.Add($"dep_iata={Uri.EscapeDataString(criteria.DepartureIata)}");

            if (!string.IsNullOrWhiteSpace(criteria.ArrivalIata))
                query.Add($"arr_iata={Uri.EscapeDataString(criteria.ArrivalIata)}");

            if (!string.IsNullOrWhiteSpace(criteria.FlightNumber))
                query.Add($"flight_iata={Uri.EscapeDataString(criteria.FlightNumber)}");

            query.Add($"dep_scheduled_time_dep={criteria.Date:yyyy-MM-dd}");

            return $"flights?{string.Join("&", query)}";
        }

        /// <summary>
        /// Maps an AviationStack <c>error</c> object to a <see cref="DomainException"/> at an
        /// appropriate HTTP status. The real provider code/message is logged; client-facing messages
        /// deliberately avoid leaking account/configuration details.
        /// </summary>
        private DomainException MapUpstreamError(JsonElement error, HttpResponseMessage response, FlightSearchCriteria criteria)
        {
            var code = TryGetString(error, "code") ?? "unknown";
            var providerMessage = TryGetString(error, "message");

            switch (code)
            {
                case "rate_limit_reached":
                case "usage_limit_reached":
                    _logger.LogWarning(
                        "AviationStack throttled flight search ({Code}: {Message}) for {Criteria}",
                        code, providerMessage, Describe(criteria));
                    return new DomainException(
                        "Flight search is temporarily unavailable due to high demand. Please try again shortly.",
                        StatusCode503ServiceUnavailable);

                case "invalid_access_key":
                case "missing_access_key":
                case "inactive_user":
                case "function_access_restricted":
                case "https_access_restricted":
                    // Our configuration problem — log loudly, never expose the cause to the client.
                    _logger.LogError(
                        "AviationStack flight search rejected our credentials/plan ({Code}: {Message})",
                        code, providerMessage);
                    return new DomainException(
                        "Flight search is temporarily unavailable due to a server configuration issue.",
                        StatusCode500InternalError);

                case "validation_error":
                    var detail = BuildValidationDetail(error, providerMessage);
                    _logger.LogWarning(
                        "AviationStack rejected flight search parameters ({Code}: {Detail}) for {Criteria}",
                        code, detail, Describe(criteria));
                    return new DomainException(
                        $"Flight search request was rejected by the flight data provider: {detail}",
                        StatusCode400BadRequest);

                default:
                    _logger.LogError(
                        "AviationStack flight search returned an error ({StatusCode} {Code}: {Message}) for {Criteria}",
                        (int)response.StatusCode, code, providerMessage, Describe(criteria));
                    return new DomainException(
                        "Flight search is temporarily unavailable. Please try again shortly.",
                        StatusCode502BadGateway);
            }
        }

        private static string BuildValidationDetail(JsonElement error, string? providerMessage)
        {
            // context: { "<field>": [ { "key": "...", "message": "..." }, ... ], ... }
            if (error.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object)
            {
                var messages = context.EnumerateObject()
                    .SelectMany(field => field.Value.ValueKind == JsonValueKind.Array
                        ? field.Value.EnumerateArray()
                        : Enumerable.Empty<JsonElement>())
                    .Select(entry => TryGetString(entry, "message"))
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .ToList();

                if (messages.Count > 0)
                    return string.Join("; ", messages);
            }

            return string.IsNullOrWhiteSpace(providerMessage) ? "invalid request parameters" : providerMessage!;
        }

        private static string Describe(FlightSearchCriteria criteria)
        {
            var route = !string.IsNullOrWhiteSpace(criteria.DepartureIata) || !string.IsNullOrWhiteSpace(criteria.ArrivalIata)
                ? $"{criteria.DepartureIata ?? "*"}->{criteria.ArrivalIata ?? "*"}"
                : null;
            var parts = new[] { route, criteria.FlightNumber, criteria.Date.ToString("yyyy-MM-dd") }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            return string.Join(" ", parts);
        }

        private static FlightSearchResult? TryMap(JsonElement flight, FlightSearchCriteria criteria)
        {
            var dep = flight.TryGetProperty("departure", out var d) ? d : default;
            var arr = flight.TryGetProperty("arrival", out var a) ? a : default;
            var flightObj = flight.TryGetProperty("flight", out var f) ? f : default;
            var airline = flight.TryGetProperty("airline", out var al) ? al : default;

            var flightNumber = TryGetString(flightObj, "iata");
            if (string.IsNullOrWhiteSpace(flightNumber)) return null;

            var airlineIata = TryGetString(airline, "iata")
                ?? (flightNumber.Length >= 2 ? flightNumber[..2] : flightNumber);

            var scheduledDep = TryGetDateTime(dep, "scheduled") ?? DateTime.UtcNow;
            var scheduledArr = TryGetDateTime(arr, "scheduled") ?? scheduledDep.AddHours(1);

            var departureIata = TryGetString(dep, "iata") ?? criteria.DepartureIata;
            var arrivalIata = TryGetString(arr, "iata") ?? criteria.ArrivalIata;

            // Route and flight number are essential to a usable result; skip entries the provider
            // returned without them (rather than fabricating empty codes).
            if (string.IsNullOrWhiteSpace(departureIata) || string.IsNullOrWhiteSpace(arrivalIata))
                return null;

            return new FlightSearchResult(
                FlightNumber: flightNumber,
                AirlineIata: airlineIata,
                DepartureIata: departureIata,
                ArrivalIata: arrivalIata,
                ServiceDate: criteria.Date,
                ScheduledDepartureUtc: scheduledDep,
                ScheduledArrivalUtc: scheduledArr,
                EquipmentIcao: TryGetString(flight.TryGetProperty("aircraft", out var ac) ? ac : default, "icao"));
        }

        private static DateTime? TryGetDateTime(JsonElement el, string field)
        {
            if (el.ValueKind == JsonValueKind.Undefined) return null;
            if (el.TryGetProperty(field, out var val) && val.ValueKind != JsonValueKind.Null)
                return DateTime.TryParse(
                    val.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var dt)
                        ? dt
                        : null;
            return null;
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
