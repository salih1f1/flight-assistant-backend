using FlightAssistant.Application.Interfaces.ExternalServices;
using System.Net.Http.Json;
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
    /// Results are filtered client-side to the requested arrival airport.
    /// </summary>
    public sealed class AeroDataBoxFlightSearchProvider : IFlightSearchProvider
    {
        private readonly HttpClient _http;

        public AeroDataBoxFlightSearchProvider(HttpClient http) => _http = http;

        public async Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            string departureIata,
            string arrivalIata,
            DateOnly date,
            CancellationToken ct = default)
        {
            // AeroDataBox uses local datetime strings for the window
            var from = $"{date:yyyy-MM-dd}T00:00";
            var to   = $"{date:yyyy-MM-dd}T23:59";
            var url  = $"flights/airports/iata/{departureIata}/{from}/{to}" +
                       "?withLeg=true&direction=Departure&withCancelled=false&withCodeshared=false&withCargo=false&withPrivate=false";

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url, ct);
            }
            catch
            {
                return [];
            }

            if (!response.IsSuccessStatusCode) return [];

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
