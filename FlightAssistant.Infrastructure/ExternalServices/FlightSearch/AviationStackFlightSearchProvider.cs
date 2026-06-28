using System.Globalization;
using System.Text.Json;
using FlightAssistant.Application.Interfaces.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.FlightSearch
{
    /// <summary>
    /// Searches for flights between two airports on a given date using the AviationStack
    /// real-time/historical flights endpoint.
    ///
    /// Endpoint: GET /v1/flights?access_key={key}&amp;dep_iata={dep}&amp;arr_iata={arr}&amp;flight_date={date}
    ///
    /// AviationStack filters by both departure and arrival airport server-side in a single
    /// request, so there is no window splitting or client-side arrival filtering. Far-future
    /// searches (more than ~7 days out) are not covered by this endpoint; the
    /// <c>/v1/flightsFuture</c> endpoint would be a later enhancement.
    /// </summary>
    public sealed class AviationStackFlightSearchProvider : IFlightSearchProvider
    {
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
            string departureIata,
            string arrivalIata,
            DateOnly date,
            CancellationToken ct = default)
        {
            var url = $"flights?access_key={_apiKey}" +
                      $"&dep_iata={departureIata}" +
                      $"&arr_iata={arrivalIata}" +
                      $"&flight_date={date:yyyy-MM-dd}";

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AviationStack flight search request failed for {Departure}->{Arrival} on {Date}",
                    departureIata, arrivalIata, date);
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "AviationStack flight search returned {StatusCode} for {Departure}->{Arrival} on {Date}",
                    (int)response.StatusCode, departureIata, arrivalIata, date);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<FlightSearchResult>();
            foreach (var flight in data.EnumerateArray())
            {
                var result = TryMap(flight, departureIata, arrivalIata, date);
                if (result is not null)
                    results.Add(result);
            }

            return results;
        }

        private static FlightSearchResult? TryMap(JsonElement flight, string depIata, string arrIata, DateOnly date)
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

            return new FlightSearchResult(
                FlightNumber: flightNumber,
                AirlineIata: airlineIata,
                DepartureIata: TryGetString(dep, "iata") ?? depIata,
                ArrivalIata: TryGetString(arr, "iata") ?? arrIata,
                ServiceDate: date,
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
