using System.Globalization;
using System.Text.Json;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.AviationStack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.FlightSearch
{
    /// <summary>
    /// Searches flights that have already operated or are operating today, using the AviationStack
    /// real-time/historical endpoint.
    ///
    /// Endpoint: GET /v1/flights?access_key={key}&amp;dep_iata={dep}&amp;arr_iata={arr}&amp;flight_iata={no}&amp;dep_scheduled_time_dep={date}
    ///
    /// This endpoint holds no future data at all — a date after today returns an empty set rather
    /// than an error — so <see cref="DateRoutingFlightSearchProvider"/> only routes past and
    /// present dates here.
    ///
    /// Codeshares are marked exactly: <c>flight.codeshared</c> names the operating flight, and a
    /// null <c>codeshared</c> identifies the operating row itself. Results are therefore grouped
    /// precisely, with no guesswork.
    ///
    /// Upstream failures are classified by <see cref="AviationStackResponse"/>. A successful
    /// response with no matching flights returns an empty list, distinct from a provider failure.
    /// </summary>
    public sealed class AviationStackLiveSearchProvider : IFlightSearchProvider
    {
        private const string Operation = "Flight search";

        // 502 Bad Gateway — the provider was unreachable.
        private const int StatusCode502BadGateway = 502;

        private readonly HttpClient _http;
        private readonly IRefDataRepository _refData;
        private readonly ILogger<AviationStackLiveSearchProvider> _logger;
        private readonly string _apiKey;

        public AviationStackLiveSearchProvider(
            HttpClient http,
            IRefDataRepository refData,
            IConfiguration config,
            ILogger<AviationStackLiveSearchProvider> logger)
        {
            _http = http;
            _refData = refData;
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

            using var doc = await AviationStackResponse.ReadOrThrowAsync(
                response, Operation, Describe(criteria), _logger, ct);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return [];

            var zones = new AirportTimeZoneResolver(_refData, _logger);
            var rows = new List<LiveRow>();
            foreach (var flight in data.EnumerateArray())
            {
                var row = TryMapRow(flight, criteria);
                if (row is null) continue;

                var converted = await ToUtcAsync(row, zones, ct);
                if (converted is not null)
                    rows.Add(converted);
            }

            return Group(rows, criteria);
        }

        /// <summary>
        /// A single provider row, before codeshares are collapsed. Times start out airport-local
        /// (see <see cref="AirportTimeZoneResolver"/>) and are filled in as UTC afterwards.
        /// </summary>
        private sealed record LiveRow(
            string FlightNumber,
            string AirlineIata,
            /// <summary>The operating flight's IATA code, or null when this row IS the operating flight.</summary>
            string? CodesharedWith,
            string DepartureIata,
            string ArrivalIata,
            DateTime ScheduledDepartureLocal,
            DateTime ScheduledArrivalLocal,
            string? DepartureTimezone,
            string? ArrivalTimezone,
            string? EquipmentIcao)
        {
            public DateTime ScheduledDepartureUtc { get; init; }
            public DateTime ScheduledArrivalUtc { get; init; }
        }

        /// <summary>
        /// Converts both ends using their own zones. Returns null when either zone is unknown, so
        /// the flight is omitted rather than reported at a wrong time.
        /// </summary>
        private static async Task<LiveRow?> ToUtcAsync(LiveRow row, AirportTimeZoneResolver zones, CancellationToken ct)
        {
            var dep = await zones.ToUtcAsync(row.ScheduledDepartureLocal, row.DepartureIata, row.DepartureTimezone, ct);
            var arr = await zones.ToUtcAsync(row.ScheduledArrivalLocal, row.ArrivalIata, row.ArrivalTimezone, ct);
            if (dep is null || arr is null) return null;

            return row with { ScheduledDepartureUtc = dep.Value, ScheduledArrivalUtc = arr.Value };
        }

        /// <summary>
        /// Collapses codeshare rows onto the flight they are marketed from. The grouping key is the
        /// operating flight's number, which the provider states outright, so distinct departures
        /// that happen to share a timetable slot are never merged.
        /// </summary>
        private static List<FlightSearchResult> Group(List<LiveRow> rows, FlightSearchCriteria criteria)
        {
            return rows
                .GroupBy(r => r.CodesharedWith ?? r.FlightNumber, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    // The operating row is the one with no `codeshared` object. It is normally
                    // present, but a flight-number search for a codeshare returns only the
                    // marketing row — in which case the number is still known from `codeshared`.
                    var operating = g.FirstOrDefault(r => r.CodesharedWith is null);
                    var anchor = operating ?? g.First();

                    var numbers = g.Select(r => r.FlightNumber)
                        .Append(g.Key)
                        .Select(n => n.ToUpperInvariant())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList();

                    return new FlightSearchResult(
                        FlightNumbers: numbers,
                        OperatingFlightNumber: g.Key.ToUpperInvariant(),
                        AirlineIata: operating?.AirlineIata
                            ?? (g.Key.Length >= 2 ? g.Key[..2].ToUpperInvariant() : g.Key.ToUpperInvariant()),
                        DepartureIata: anchor.DepartureIata,
                        ArrivalIata: anchor.ArrivalIata,
                        ServiceDate: criteria.Date,
                        ScheduledDepartureUtc: anchor.ScheduledDepartureUtc,
                        ScheduledArrivalUtc: anchor.ScheduledArrivalUtc,
                        EquipmentIcao: g.Select(r => r.EquipmentIcao).FirstOrDefault(e => e is not null));
                })
                .OrderBy(r => r.ScheduledDepartureUtc)
                .ToList();
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

        private static LiveRow? TryMapRow(JsonElement flight, FlightSearchCriteria criteria)
        {
            var dep = flight.TryGetProperty("departure", out var d) ? d : default;
            var arr = flight.TryGetProperty("arrival", out var a) ? a : default;
            var flightObj = flight.TryGetProperty("flight", out var f) ? f : default;
            var airline = flight.TryGetProperty("airline", out var al) ? al : default;

            var flightNumber = TryGetString(flightObj, "iata");
            if (string.IsNullOrWhiteSpace(flightNumber)) return null;

            var airlineIata = TryGetString(airline, "iata")
                ?? (flightNumber.Length >= 2 ? flightNumber[..2] : flightNumber);

            var scheduledDep = TryGetLocalDateTime(dep, "scheduled");
            var scheduledArr = TryGetLocalDateTime(arr, "scheduled");
            if (scheduledDep is null || scheduledArr is null) return null;

            var departureIata = TryGetString(dep, "iata") ?? criteria.DepartureIata;
            var arrivalIata = TryGetString(arr, "iata") ?? criteria.ArrivalIata;

            // Route and flight number are essential to a usable result; skip entries the provider
            // returned without them (rather than fabricating empty codes).
            if (string.IsNullOrWhiteSpace(departureIata) || string.IsNullOrWhiteSpace(arrivalIata))
                return null;

            // `codeshared` is an object naming the operating flight, or null on the operating row.
            string? codesharedWith = null;
            if (flightObj.ValueKind == JsonValueKind.Object &&
                flightObj.TryGetProperty("codeshared", out var cs) &&
                cs.ValueKind == JsonValueKind.Object)
            {
                codesharedWith = TryGetString(cs, "flight_iata");
            }

            return new LiveRow(
                FlightNumber: flightNumber.ToUpperInvariant(),
                AirlineIata: airlineIata.ToUpperInvariant(),
                CodesharedWith: codesharedWith?.ToUpperInvariant(),
                DepartureIata: departureIata.ToUpperInvariant(),
                ArrivalIata: arrivalIata.ToUpperInvariant(),
                ScheduledDepartureLocal: scheduledDep.Value,
                ScheduledArrivalLocal: scheduledArr.Value,
                DepartureTimezone: TryGetString(dep, "timezone"),
                ArrivalTimezone: TryGetString(arr, "timezone"),
                EquipmentIcao: TryGetString(flight.TryGetProperty("aircraft", out var ac) ? ac : default, "icao"));
        }

        /// <summary>
        /// Reads a timestamp as the airport's wall-clock time, deliberately discarding the offset.
        /// AviationStack stamps these "+00:00" but the value is local: LHR→JFK reads as
        /// 20:05 → 22:50, which is a 7h45m flight in local terms and an impossible 2h45m in UTC.
        /// </summary>
        private static DateTime? TryGetLocalDateTime(JsonElement el, string field)
        {
            if (el.ValueKind == JsonValueKind.Undefined) return null;
            if (el.TryGetProperty(field, out var val) && val.ValueKind == JsonValueKind.String)
                // DateTimeOffset.TryParse().DateTime yields the wall-clock time exactly as written.
                // DateTime.TryParse would instead rebase an offset-bearing string onto the *host
                // machine's* timezone, making results depend on where the server runs.
                return DateTimeOffset.TryParse(
                    val.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dto)
                        ? DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified)
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
