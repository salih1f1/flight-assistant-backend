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
    /// Searches future-dated flights using the AviationStack schedules endpoint.
    ///
    /// Endpoint: GET /v1/flightsFuture?access_key={key}&amp;iataCode={airport}&amp;type=departure&amp;date={date}
    ///
    /// This endpoint differs from the real-time one in ways that shape everything below, all
    /// established by probing since the vendor does not document them:
    ///
    /// <list type="bullet">
    /// <item>An airport is mandatory — omitting <c>iataCode</c> is a validation error. A search by
    /// flight number alone therefore resolves a departure airport first, via
    /// <see cref="AviationStackRouteResolver"/>.</item>
    /// <item>Neither <c>arr_iata</c> nor <c>flight_iata</c> filter anything; both are silently
    /// ignored. Route and flight-number matching happen here, client-side.</item>
    /// <item>Times are airport-local with no offset, so they are converted using each airport's
    /// IANA timezone from reference data.</item>
    /// <item>All values are lowercase.</item>
    /// <item>There is no codeshare marker, so <c>OperatingFlightNumber</c> is left null rather than
    /// guessed. Rows are grouped by route and scheduled times instead.</item>
    /// </list>
    /// </summary>
    public sealed class AviationStackScheduleSearchProvider : IFlightSearchProvider
    {
        private const string Operation = "Flight search";

        // 502 Bad Gateway — the provider was unreachable.
        private const int StatusCode502BadGateway = 502;

        // 500 Internal Server Error — our own reference data cannot place the flights in time.
        private const int StatusCode500InternalError = 500;

        private readonly HttpClient _http;
        private readonly AviationStackRouteResolver _routes;
        private readonly IRefDataRepository _refData;
        private readonly AviationStackAirportTimezoneLookup _timezones;
        private readonly ILogger<AviationStackScheduleSearchProvider> _logger;
        private readonly string _apiKey;

        public AviationStackScheduleSearchProvider(
            HttpClient http,
            AviationStackRouteResolver routes,
            IRefDataRepository refData,
            AviationStackAirportTimezoneLookup timezones,
            IConfiguration config,
            ILogger<AviationStackScheduleSearchProvider> logger)
        {
            _http = http;
            _routes = routes;
            _refData = refData;
            _timezones = timezones;
            _logger = logger;
            _apiKey = config["AviationStack:ApiKey"] ?? string.Empty;
        }

        public async Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default)
        {
            // The endpoint is anchored on an airport. A route search supplies one directly; a
            // flight-number search has to be resolved to one first.
            string? airport = criteria.DepartureIata;
            string? fallbackTimezone = null;

            if (string.IsNullOrWhiteSpace(airport))
            {
                if (string.IsNullOrWhiteSpace(criteria.FlightNumber))
                    return [];

                var resolved = await _routes.ResolveDepartureAsync(criteria.FlightNumber, ct);
                if (resolved is null)
                    return [];

                airport = resolved.DepartureIata;
                fallbackTimezone = resolved.Timezone;
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(BuildUrl(airport, criteria), ct);
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

            var zones = new AirportTimeZoneResolver(_refData, _timezones, _logger);
            var rows = new List<ScheduleRow>();
            var matched = 0;

            foreach (var element in data.EnumerateArray())
            {
                var row = TryMapRow(element, criteria);
                if (row is null) continue;
                if (!Matches(row, criteria)) continue;

                matched++;
                var converted = await ToUtcAsync(row, zones, fallbackTimezone, ct);
                if (converted is not null)
                    rows.Add(converted);
            }

            ThrowIfEverythingWasDroppedForTimezones(rows, matched, zones, criteria);

            return Group(rows, criteria);
        }

        /// <summary>
        /// Distinguishes "this route has no flights" from "we could not place any of the flights we
        /// received in time". Returning an empty list for the latter is what made the original
        /// defect so hard to spot, so it is now an error naming the airports at fault.
        /// </summary>
        private void ThrowIfEverythingWasDroppedForTimezones(
            List<ScheduleRow> kept, int matched, AirportTimeZoneResolver zones, FlightSearchCriteria criteria)
        {
            if (kept.Count > 0 || matched == 0 || zones.UnresolvedAirports.Count == 0) return;

            var airports = string.Join(", ", zones.UnresolvedAirports);
            _logger.LogError(
                "Dropped all {Count} matching flights for {Criteria}: no timezone for {Airports}",
                matched, Describe(criteria), airports);

            throw new DomainException(
                "Flight search is unavailable for this route because the server is missing timezone data for one of its airports.",
                StatusCode500InternalError,
                $"No IANA timezone could be resolved for: {airports}. Times could not be converted to UTC, "
                + "so every matching flight was discarded. Backfill Airport.Timezone for these airports.");
        }

        /// <summary>A schedules row with times still in airport-local form.</summary>
        private sealed record ScheduleRow(
            string FlightNumber,
            string AirlineIata,
            string DepartureIata,
            string ArrivalIata,
            DateTime ScheduledDepartureLocal,
            DateTime ScheduledArrivalLocal,
            string? EquipmentIcao)
        {
            public DateTime ScheduledDepartureUtc { get; init; }
            public DateTime ScheduledArrivalUtc { get; init; }
        }

        /// <summary>
        /// Groups rows into physical departures. Without a codeshare marker the only available
        /// signal is that codeshares share a route and both scheduled times exactly, which is what
        /// the provider's own data shows: the TK1028 and 6E4120 records for a given date are
        /// identical apart from their airline and flight blocks.
        /// </summary>
        private static List<FlightSearchResult> Group(List<ScheduleRow> rows, FlightSearchCriteria criteria)
        {
            return rows
                .GroupBy(r => (r.DepartureIata, r.ArrivalIata, r.ScheduledDepartureUtc, r.ScheduledArrivalUtc))
                .Select(g => new FlightSearchResult(
                    FlightNumbers: g.Select(r => r.FlightNumber)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList(),
                    // The schedules endpoint does not say which carrier operates the aircraft, and
                    // picking one would be a guess presented as fact.
                    OperatingFlightNumber: null,
                    AirlineIata: null,
                    DepartureIata: g.Key.DepartureIata,
                    ArrivalIata: g.Key.ArrivalIata,
                    ServiceDate: criteria.Date,
                    ScheduledDepartureUtc: g.Key.ScheduledDepartureUtc,
                    ScheduledArrivalUtc: g.Key.ScheduledArrivalUtc,
                    EquipmentIcao: g.Select(r => r.EquipmentIcao).FirstOrDefault(e => e is not null)))
                .OrderBy(r => r.ScheduledDepartureUtc)
                .ToList();
        }

        /// <summary>
        /// Applies the filters the provider ignores. A flight-number search matches any number on
        /// the departure, so a traveler holding a codeshare ticket still finds their flight.
        /// </summary>
        private static bool Matches(ScheduleRow row, FlightSearchCriteria criteria)
        {
            if (!string.IsNullOrWhiteSpace(criteria.DepartureIata) &&
                !row.DepartureIata.Equals(criteria.DepartureIata, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(criteria.ArrivalIata) &&
                !row.ArrivalIata.Equals(criteria.ArrivalIata, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(criteria.FlightNumber) &&
                !row.FlightNumber.Equals(criteria.FlightNumber, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        /// <summary>
        /// Converts both local times to UTC using each end's own timezone — a flight crossing zones
        /// departs and arrives in different ones. Returns null when a zone cannot be established,
        /// because emitting a time that is silently hours wrong is worse than omitting the flight.
        /// </summary>
        private static async Task<ScheduleRow?> ToUtcAsync(
            ScheduleRow row, AirportTimeZoneResolver zones, string? fallbackTimezone, CancellationToken ct)
        {
            // The schedules payload carries no timezone of its own; the only hint available is the
            // one the route lookup returned for the departure airport, when that path was taken.
            var dep = await zones.ToUtcAsync(row.ScheduledDepartureLocal, row.DepartureIata, fallbackTimezone, ct);
            var arr = await zones.ToUtcAsync(row.ScheduledArrivalLocal, row.ArrivalIata, null, ct);
            if (dep is null || arr is null) return null;

            return row with { ScheduledDepartureUtc = dep.Value, ScheduledArrivalUtc = arr.Value };
        }

        private string BuildUrl(string airport, FlightSearchCriteria criteria)
        {
            var query = new List<string>
            {
                $"access_key={Uri.EscapeDataString(_apiKey)}",
                $"iataCode={Uri.EscapeDataString(airport)}",
                "type=departure",
                $"date={criteria.Date:yyyy-MM-dd}"
            };

            // The one filter this endpoint honours server-side, and it meaningfully shrinks the
            // payload (106 rows down to 3 on a tested airport).
            if (!string.IsNullOrWhiteSpace(criteria.FlightNumber) && criteria.FlightNumber.Length >= 2)
                query.Add($"airline_iata={Uri.EscapeDataString(criteria.FlightNumber[..2])}");

            return $"flightsFuture?{string.Join("&", query)}";
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

        private static ScheduleRow? TryMapRow(JsonElement element, FlightSearchCriteria criteria)
        {
            var dep = element.TryGetProperty("departure", out var d) ? d : default;
            var arr = element.TryGetProperty("arrival", out var a) ? a : default;
            var flight = element.TryGetProperty("flight", out var f) ? f : default;
            var airline = element.TryGetProperty("airline", out var al) ? al : default;

            var flightNumber = TryGetString(flight, "iataNumber");
            if (string.IsNullOrWhiteSpace(flightNumber)) return null;

            var departureIata = TryGetString(dep, "iataCode");
            var arrivalIata = TryGetString(arr, "iataCode");
            if (string.IsNullOrWhiteSpace(departureIata) || string.IsNullOrWhiteSpace(arrivalIata))
                return null;

            var depLocal = TryGetLocalDateTime(dep, "scheduledTime");
            var arrLocal = TryGetLocalDateTime(arr, "scheduledTime");
            if (depLocal is null || arrLocal is null) return null;

            var airlineIata = TryGetString(airline, "iataCode")
                ?? (flightNumber.Length >= 2 ? flightNumber[..2] : flightNumber);

            return new ScheduleRow(
                FlightNumber: flightNumber.ToUpperInvariant(),
                AirlineIata: airlineIata.ToUpperInvariant(),
                DepartureIata: departureIata.ToUpperInvariant(),
                ArrivalIata: arrivalIata.ToUpperInvariant(),
                ScheduledDepartureLocal: depLocal.Value,
                ScheduledArrivalLocal: arrLocal.Value,
                EquipmentIcao: TryGetString(element.TryGetProperty("aircraft", out var ac) ? ac : default, "modelCode")
                    ?.ToUpperInvariant());
        }

        /// <summary>
        /// Parses "2026-10-10 09:35:00". The value carries no offset and must NOT be treated as
        /// UTC — it is airport-local and is converted later.
        /// </summary>
        private static DateTime? TryGetLocalDateTime(JsonElement el, string field)
        {
            if (el.ValueKind == JsonValueKind.Undefined) return null;
            if (el.TryGetProperty(field, out var val) && val.ValueKind == JsonValueKind.String)
                // Parsed via DateTimeOffset so the wall-clock time survives verbatim, matching the
                // live provider and staying independent of the host machine's timezone.
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
