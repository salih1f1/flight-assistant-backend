using System.Globalization;
using System.Text.Json;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.AviationStack;
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
    /// Upstream failures are classified by <see cref="AviationStackResponse"/>, which is shared with
    /// the flight status provider. A successful response with no matching flights returns an empty
    /// list, which is distinct from a provider failure.
    /// </summary>
    public sealed class AviationStackFlightSearchProvider : IFlightSearchProvider
    {
        private const string Operation = "Flight search";

        // 502 Bad Gateway — the provider was unreachable.
        private const int StatusCode502BadGateway = 502;

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

            using (var doc = await AviationStackResponse.ReadOrThrowAsync(
                response, Operation, Describe(criteria), _logger, ct))
            {
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
