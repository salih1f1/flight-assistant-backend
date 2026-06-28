using FlightAssistant.Application.Interfaces.ExternalServices;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace FlightAssistant.Infrastructure.ExternalServices.FlightSearch
{
    /// <summary>
    /// Searches for flights between two airports on a given date using the AeroDataBox
    /// airport departures endpoint.
    ///
    /// Endpoint: GET /flights/airports/iata/{iata}/{fromLocal}/{toLocal}
    ///           ?withLeg=true&direction=Departure&withCancelled=false&withCodeshared=false
    ///
    /// The endpoint rejects any window longer than 12 hours, so a full service day is
    /// queried as two consecutive windows (00:00–11:59 and 12:00–23:59) and merged.
    ///
    /// Results are filtered client-side to the requested arrival airport.
    /// </summary>
    public sealed class AeroDataBoxFlightSearchProvider : IFlightSearchProvider
    {
        private readonly HttpClient _http;
        private readonly ILogger<AeroDataBoxFlightSearchProvider> _logger;

        // AeroDataBox allows a maximum search window of 12 hours per request.
        private static readonly (string From, string To)[] DayWindows =
        [
            ("00:00", "11:59"),
            ("12:00", "23:59"),
        ];

        public AeroDataBoxFlightSearchProvider(HttpClient http, ILogger<AeroDataBoxFlightSearchProvider> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            string departureIata,
            string arrivalIata,
            DateOnly date,
            CancellationToken ct = default)
        {
            // Dedupe across the two windows by flight number + scheduled departure, in case
            // the boundary returns the same flight twice.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<FlightSearchResult>();

            foreach (var (from, to) in DayWindows)
            {
                var window = await SearchWindowAsync(departureIata, arrivalIata, date, from, to, ct);
                foreach (var result in window)
                {
                    var key = $"{result.FlightNumber}|{result.ScheduledDepartureUtc:O}";
                    if (seen.Add(key))
                        results.Add(result);
                }
            }

            return results;
        }

        private async Task<IReadOnlyList<FlightSearchResult>> SearchWindowAsync(
            string departureIata,
            string arrivalIata,
            DateOnly date,
            string fromTime,
            string toTime,
            CancellationToken ct)
        {
            // AeroDataBox uses local datetime strings for the window
            var from = $"{date:yyyy-MM-dd}T{fromTime}";
            var to   = $"{date:yyyy-MM-dd}T{toTime}";
            var url  = $"flights/airports/iata/{departureIata}/{from}/{to}" +
                       "?withLeg=true&direction=Departure&withCancelled=false&withCodeshared=false&withCargo=false&withPrivate=false";

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AeroDataBox flight search request failed for {Departure}->{Arrival} on {Date} ({From}-{To})",
                    departureIata, arrivalIata, date, fromTime, toTime);
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "AeroDataBox flight search returned {StatusCode} for {Departure}->{Arrival} on {Date} ({From}-{To}): {Body}",
                    (int)response.StatusCode, departureIata, arrivalIata, date, fromTime, toTime, body);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            // Response: { "departures": [ {...}, ... ] }
            if (!doc.RootElement.TryGetProperty("departures", out var departures) ||
                departures.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<FlightSearchResult>();
            foreach (var flight in departures.EnumerateArray())
            {
                var result = TryMap(flight, departureIata, arrivalIata, date);
                if (result is not null)
                    results.Add(result);
            }

            return results;
        }

        private static FlightSearchResult? TryMap(JsonElement flight, string depIata, string arrIata, DateOnly date)
        {
            // Filter by arrival airport
            var actualArrIata = TryGetString(flight, "arrival", "airport", "iata");
            if (actualArrIata is null || !actualArrIata.Equals(arrIata, StringComparison.OrdinalIgnoreCase))
                return null;

            var flightNumber = flight.TryGetProperty("number", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(flightNumber)) return null;

            var airlineIata = TryGetString(flight, "airline", "iata") ?? flightNumber[..2];

            var scheduledDep = TryGetDateTime(flight, "departure", "scheduledTimeUtc") ?? DateTime.UtcNow;
            var scheduledArr = TryGetDateTime(flight, "arrival", "scheduledTimeUtc") ?? scheduledDep.AddHours(1);

            var equipment = TryGetString(flight, "aircraft", "model");

            return new FlightSearchResult(
                FlightNumber: flightNumber,
                AirlineIata: airlineIata,
                DepartureIata: depIata,
                ArrivalIata: arrIata,
                ServiceDate: date,
                ScheduledDepartureUtc: scheduledDep,
                ScheduledArrivalUtc: scheduledArr,
                EquipmentIcao: equipment);
        }

        private static DateTime? TryGetDateTime(JsonElement el, string section, string field)
        {
            if (el.TryGetProperty(section, out var sec) && sec.TryGetProperty(field, out var val))
                return DateTime.TryParse(val.GetString(), out var dt) ? dt.ToUniversalTime() : null;
            return null;
        }

        private static string? TryGetString(JsonElement el, params string[] path)
        {
            var current = el;
            foreach (var key in path)
            {
                if (!current.TryGetProperty(key, out current)) return null;
            }
            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }
    }
}
