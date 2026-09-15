using System.Globalization;
using System.Text.Json;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Searches flights using FlightAware AeroAPI's schedules endpoint.
    ///
    /// Endpoint: GET /schedules/{date_start}/{date_end}?origin=&amp;destination=&amp;airline=&amp;
    /// flight_number=&amp;include_codeshares=true&amp;max_pages=1&amp;cursor=
    ///
    /// One endpoint covers the full −3&#160;month → +12&#160;month range (no live/future split), and
    /// <c>origin</c>/<c>destination</c>/<c>airline</c>/<c>flight_number</c> all filter server-side,
    /// so there is no client-side route/flight-number filtering. Returns one result per flight
    /// designator — including codeshares when <see cref="FlightSearchCriteria.IncludeCodeshares"/>
    /// is set — with no operating-carrier grouping: the caller derives the marketing airline from
    /// each result's own <see cref="FlightSearchResult.FlightNumber"/>.
    ///
    /// <para>
    /// <b>The UTC/local-day mismatch.</b> <c>scheduled_out</c>/<c>scheduled_in</c> are UTC, but
    /// <see cref="FlightSearchCriteria.Date"/> is a local service date at the <b>origin</b>
    /// airport. A naive <c>[D, D+1)</c> UTC query is wrong by as much as ~46% for a far-east
    /// airport (proven by the recorded AKL→SYD fixtures) — it returns rows that actually depart
    /// the next local day and misses rows that depart the requested local day but before the UTC
    /// day rolls over. The fix is to shift the window, not widen it: resolve the origin's IANA
    /// zone via <see cref="AeroApiAirportTimezoneLookup"/>, then pass the exact UTC instants of
    /// local midnight and next-local-midnight as <c>date_start</c>/<c>date_end</c>. The window
    /// stays a strict 24 hours, so page count — and therefore cost — is unaffected.
    /// </para>
    ///
    /// <para>
    /// <b>When the origin (or its zone) is unknown.</b> A flight-number-only search may carry no
    /// <see cref="FlightSearchCriteria.DepartureIata"/> at all (AeroAPI does not require one — an
    /// <c>airline</c>+<c>flight_number</c> pair is a valid query on its own), and even a known
    /// origin's zone can fail to resolve. Either way there is no local zone to anchor a precise
    /// window on, so this widens to <c>[D−1, D+2)</c> UTC (three calendar days covers every
    /// possible UTC offset) and, after the results come back, filters each row using <i>its own</i>
    /// <c>origin_iata</c>'s zone — resolved and cached the same way, and now knowable because the
    /// row supplies it. If a row's own zone still cannot be resolved even at that point, the row is
    /// kept rather than silently dropped (the philosophy #275 established: an unplaceable time is
    /// worse omitted than approximated), falling back to comparing the UTC calendar date directly
    /// as a last-resort approximation.
    /// </para>
    /// </summary>
    public sealed class AeroApiFlightSearchProvider : IFlightSearchProvider
    {
        private const string Operation = "Flight search";

        // 502 Bad Gateway — the provider was unreachable (no HTTP response at all).
        private const int StatusCode502BadGateway = 502;

        private readonly HttpClient _http;
        private readonly AeroApiAirportTimezoneLookup _timezones;
        private readonly ILogger<AeroApiFlightSearchProvider> _logger;
        private readonly string _apiKey;
        private readonly int _maxPages;

        public AeroApiFlightSearchProvider(
            HttpClient http,
            AeroApiAirportTimezoneLookup timezones,
            IOptions<AeroApiOptions> options,
            ILogger<AeroApiFlightSearchProvider> logger)
        {
            _http = http;
            _timezones = timezones;
            _logger = logger;
            _apiKey = options.Value.ApiKey;
            _maxPages = options.Value.MaxPages;
        }

        public async Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default)
        {
            // A single-airport query (one end of a route and nothing else) is ~700 rows and roughly
            // 4% of a monthly quota in one call — never issue it, at either end. Destination-only is
            // the exact mirror of origin-only and is in fact worse: with no origin there is no zone
            // to anchor a local day on, so it runs over the widened 3-day window (see
            // ResolveWindowAsync) — three times the rows.
            //
            // The flight number only counts as a filter if BuildUrl can actually push it down,
            // which needs at least the 2-character airline prefix. A shorter non-empty value would
            // otherwise satisfy this guard while contributing nothing to the query — the same
            // fail-open shape, reached by a different route.
            var hasOrigin = !string.IsNullOrWhiteSpace(criteria.DepartureIata);
            var hasDestination = !string.IsNullOrWhiteSpace(criteria.ArrivalIata);
            var hasUsableFlightNumber = HasUsableFlightNumber(criteria.FlightNumber);

            if (!hasUsableFlightNumber && !(hasOrigin && hasDestination))
            {
                _logger.LogWarning("Rejected an unfiltered flight search for {Criteria}", Describe(criteria));
                throw new DomainException(
                    "A flight search must include both a departure and an arrival airport, or a flight number.",
                    400);
            }

            var window = await ResolveWindowAsync(criteria.DepartureIata, criteria.Date, ct);

            var rows = new List<Row>();
            var pagesFetched = 0;
            string? nextUrl = BuildUrl(window.StartUtc, window.EndUtc, criteria);

            while (nextUrl is not null)
            {
                if (pagesFetched >= _maxPages)
                {
                    _logger.LogWarning(
                        "AeroAPI flight search hit the {MaxPages}-page cap with more results remaining for {Criteria}",
                        _maxPages, Describe(criteria));
                    break;
                }

                HttpResponseMessage response;
                try
                {
                    response = await _http.GetAsync(nextUrl, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "AeroAPI flight search request failed for {Criteria}", Describe(criteria));
                    throw new DomainException(
                        $"{Operation} is temporarily unavailable. Please try again shortly.",
                        ex,
                        StatusCode502BadGateway);
                }

                using var doc = await AeroApiResponse.ReadOrThrowAsync(
                    response, Operation, Describe(criteria), _apiKey, _logger, ct);
                pagesFetched++;

                if (doc.RootElement.TryGetProperty("scheduled", out var scheduled) &&
                    scheduled.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in scheduled.EnumerateArray())
                    {
                        var row = TryMapRow(element);
                        if (row is not null) rows.Add(row);
                    }
                }

                nextUrl = TryGetNextUrl(doc);
            }

            if (!window.IsPreciseLocalDay)
                rows = await FilterToIntendedLocalDateAsync(rows, criteria.Date, ct);

            return rows
                .Select(r => ToResult(r, criteria.Date))
                .OrderBy(r => r.ScheduledDepartureUtc)
                .ToList();
        }

        /// <summary>A UTC query window plus whether it precisely bounds the origin's local day.</summary>
        private readonly record struct SearchWindow(DateTime StartUtc, DateTime EndUtc, bool IsPreciseLocalDay);

        private async Task<SearchWindow> ResolveWindowAsync(string? originIata, DateOnly date, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(originIata))
            {
                _logger.LogWarning(
                    "Flight search for {Date} has no origin airport to anchor a local day on; using a widened UTC window and per-row date filtering",
                    date);
                return WidenedWindow(date);
            }

            var timezoneId = await _timezones.TryGetTimezoneAsync(originIata, ct);
            if (string.IsNullOrWhiteSpace(timezoneId))
            {
                _logger.LogWarning(
                    "No IANA timezone resolved for origin {Origin}; using a widened UTC window for {Date} and per-row date filtering",
                    originIata, date);
                return WidenedWindow(date);
            }

            TimeZoneInfo zone;
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                _logger.LogWarning(ex,
                    "Timezone id {TimezoneId} for origin {Origin} could not be loaded; using a widened UTC window for {Date}",
                    timezoneId, originIata, date);
                return WidenedWindow(date);
            }

            var localMidnight = date.ToDateTime(TimeOnly.MinValue);
            var nextLocalMidnight = date.AddDays(1).ToDateTime(TimeOnly.MinValue);

            // Prefer the earlier UTC instant for the window's start and the later one for its end,
            // so an ambiguous or nonexistent local midnight (a DST transition) only ever makes the
            // window very slightly *wider*, never narrower — a wall-clock time that doesn't exist
            // can't drop a real flight, and one that occurs twice must not exclude either occurrence.
            var startUtc = ResolveLocalToUtc(zone, localMidnight, preferLater: false);
            var endUtc = ResolveLocalToUtc(zone, nextLocalMidnight, preferLater: true);

            return new SearchWindow(startUtc, endUtc, IsPreciseLocalDay: true);
        }

        private static SearchWindow WidenedWindow(DateOnly date) => new(
            DateTime.SpecifyKind(date.AddDays(-1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            DateTime.SpecifyKind(date.AddDays(2).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            IsPreciseLocalDay: false);

        /// <summary>
        /// Converts a local wall-clock time to UTC, handling the two ways a DST transition can make
        /// it not map to a single instant. <paramref name="preferLater"/> picks which of two valid
        /// UTC instants to use when the local time is ambiguous (occurs twice, on "fall back");
        /// when it is invalid (skipped entirely, on "spring forward") the value is advanced minute
        /// by minute to the first moment that exists — nothing could have been scheduled at a
        /// wall-clock time that never happened, so this never discards a real row.
        /// </summary>
        /// <summary>Internal (not private) so <c>AeroApiFlightSearchProviderTests</c> can drive the
        /// DST edge cases directly against a synthetic <see cref="TimeZoneInfo"/> with a transition
        /// exactly at local midnight — real-world zones almost never transition then, precisely to
        /// avoid this ambiguity, so there is no real IANA id that exercises both branches on demand.</summary>
        internal static DateTime ResolveLocalToUtc(TimeZoneInfo zone, DateTime local, bool preferLater)
        {
            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

            if (zone.IsInvalidTime(unspecified))
            {
                var probe = unspecified;
                var guard = 0;
                while (zone.IsInvalidTime(probe) && guard < 24 * 60)
                {
                    probe = probe.AddMinutes(1);
                    guard++;
                }
                return TimeZoneInfo.ConvertTimeToUtc(probe, zone);
            }

            if (zone.IsAmbiguousTime(unspecified))
            {
                var offsets = zone.GetAmbiguousTimeOffsets(unspecified);
                var candidates = offsets
                    .Select(o => new DateTimeOffset(unspecified, o).UtcDateTime)
                    .ToArray();
                return preferLater ? candidates.Max() : candidates.Min();
            }

            return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
        }

        /// <summary>
        /// Applied only when the query window was widened for lack of a precise origin zone. Each
        /// row is re-checked against its own <c>origin_iata</c> — which the widened query cannot
        /// filter server-side, since criteria may not have named an origin at all — rather than
        /// dropped wholesale.
        /// </summary>
        private async Task<List<Row>> FilterToIntendedLocalDateAsync(List<Row> rows, DateOnly date, CancellationToken ct)
        {
            // One lookup per distinct origin, not per row: otherwise an origin whose lookup fails costs a
            // billed /airports call for every one of its rows (#373). A miss is remembered too.
            var zones = new Dictionary<string, TimeZoneInfo?>(StringComparer.OrdinalIgnoreCase);

            var kept = new List<Row>();
            foreach (var row in rows)
            {
                if (!zones.TryGetValue(row.OriginIata, out var zone))
                {
                    zone = await TryResolveZoneAsync(row.OriginIata, ct);
                    zones[row.OriginIata] = zone;
                }

                if (zone is not null)
                {
                    var local = TimeZoneInfo.ConvertTimeFromUtc(
                        DateTime.SpecifyKind(row.ScheduledOutUtc, DateTimeKind.Utc), zone);
                    if (DateOnly.FromDateTime(local) == date) kept.Add(row);
                    continue;
                }

                // Still no zone available even now — approximate with the UTC calendar date rather
                // than drop the row outright. Wrong for a large-offset airport, but that is the
                // documented, logged cost of a genuinely unresolvable zone, not a silent loss.
                if (DateOnly.FromDateTime(row.ScheduledOutUtc) == date)
                    kept.Add(row);
            }
            return kept;
        }

        private async Task<TimeZoneInfo?> TryResolveZoneAsync(string originIata, CancellationToken ct)
        {
            var timezoneId = await _timezones.TryGetTimezoneAsync(originIata, ct);
            if (string.IsNullOrWhiteSpace(timezoneId)) return null;

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                _logger.LogWarning(ex, "Timezone id {TimezoneId} for origin {Origin} could not be loaded", timezoneId, originIata);
                return null;
            }
        }

        /// <summary>A schedules row exactly as AeroAPI returns it, before mapping to the public shape.</summary>
        private sealed record Row(
            string IdentIata,
            string OriginIata,
            string DestinationIata,
            DateTime ScheduledOutUtc,
            DateTime ScheduledInUtc);

        private static FlightSearchResult ToResult(Row row, DateOnly serviceDate) =>
            new(
                FlightNumber: row.IdentIata.ToUpperInvariant(),
                ServiceDate: serviceDate,
                DepartureAirportIata: row.OriginIata.ToUpperInvariant(),
                ArrivalAirportIata: row.DestinationIata.ToUpperInvariant(),
                ScheduledDepartureUtc: row.ScheduledOutUtc,
                ScheduledArrivalUtc: row.ScheduledInUtc);

        /// <summary>
        /// Skips rows missing anything required to place them: <c>ident_iata</c>, <c>origin_iata</c>,
        /// <c>destination_iata</c>, <c>scheduled_out</c> or <c>scheduled_in</c>. A malformed row is
        /// omitted, never allowed to throw and fail the whole search.
        /// </summary>
        private static Row? TryMapRow(JsonElement element)
        {
            var identIata = TryGetString(element, "ident_iata");
            var originIata = TryGetString(element, "origin_iata");
            var destinationIata = TryGetString(element, "destination_iata");
            var scheduledOut = TryGetUtcDateTime(element, "scheduled_out");
            var scheduledIn = TryGetUtcDateTime(element, "scheduled_in");

            if (string.IsNullOrWhiteSpace(identIata) ||
                string.IsNullOrWhiteSpace(originIata) ||
                string.IsNullOrWhiteSpace(destinationIata) ||
                scheduledOut is null ||
                scheduledIn is null)
                return null;

            return new Row(
                IdentIata: identIata,
                OriginIata: originIata,
                DestinationIata: destinationIata,
                ScheduledOutUtc: scheduledOut.Value,
                ScheduledInUtc: scheduledIn.Value);
        }

        /// <summary>
        /// Extracts the next page's path from a response's <c>links.next</c>, or null to stop
        /// paging.
        ///
        /// <para>
        /// <b>The link is untrusted input.</b> The "AeroApi" <see cref="HttpClient"/> carries
        /// <c>x-apikey</c> as a default header on <i>every</i> request it makes, regardless of
        /// host, so following a link that leaves the configured base address would hand the API
        /// credential to whatever host the link names. A malformed, redirect-shaped or tampered
        /// <c>next</c> is therefore refused outright: it is resolved against
        /// <see cref="HttpClient.BaseAddress"/> and followed only if the result still sits
        /// underneath it. That single test catches every escape at once — an absolute URI resolves
        /// to itself and so lands outside the base, a <c>../</c> climbs out of it, and a
        /// host-rooted path leaves the <c>/aeroapi/</c> prefix behind. Refusing costs at most the
        /// remaining pages of one search; the alternative costs the key.
        /// </para>
        ///
        /// <para>
        /// Note that "does it parse as absolute?" is deliberately <i>not</i> the test:
        /// <c>Uri.TryCreate("/schedules/…", UriKind.Absolute, …)</c> succeeds on Unix, yielding a
        /// <c>file://</c> URI, so that check would reject AeroAPI's own perfectly ordinary
        /// host-rooted links. Resolution against the base is both stricter and correct.
        /// </para>
        /// </summary>
        private string? TryGetNextUrl(JsonDocument doc)
        {
            if (!doc.RootElement.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Object)
                return null;
            if (!links.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
                return null;

            var value = next.GetString();
            if (string.IsNullOrWhiteSpace(value)) return null;

            // AeroAPI's "next" link is a path rooted at the API host (e.g.
            // "/schedules/2026-09-29/2026-09-30?..."), but this client sends every other request as
            // relative to the "AeroApi" HttpClient's BaseAddress (which already ends in "/aeroapi/").
            // Combining a leading-slash relative URI against that base would discard the "/aeroapi/"
            // segment (standard URI combination rules), so the leading slash is stripped to keep the
            // request under the same base every other call in this class uses.
            var relative = value.TrimStart('/');

            var baseAddress = _http.BaseAddress;
            if (baseAddress is null ||
                !Uri.TryCreate(baseAddress, relative, out var resolved) ||
                !baseAddress.IsBaseOf(resolved))
            {
                _logger.LogWarning(
                    "AeroAPI returned a paging link that does not resolve underneath the configured base address; refusing to follow it (it could send the API key off-origin) and stopping pagination");
                return null;
            }

            return relative;
        }

        private string BuildUrl(DateTime startUtc, DateTime endUtc, FlightSearchCriteria criteria)
        {
            var start = startUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var end = endUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var query = new List<string>
            {
                $"include_codeshares={(criteria.IncludeCodeshares ? "true" : "false")}",
                "max_pages=1"
            };

            if (!string.IsNullOrWhiteSpace(criteria.DepartureIata))
                query.Add($"origin={Uri.EscapeDataString(criteria.DepartureIata.ToUpperInvariant())}");

            if (!string.IsNullOrWhiteSpace(criteria.ArrivalIata))
                query.Add($"destination={Uri.EscapeDataString(criteria.ArrivalIata.ToUpperInvariant())}");

            if (HasUsableFlightNumber(criteria.FlightNumber))
            {
                var flightNumber = criteria.FlightNumber!.Trim();
                var airline = flightNumber[..2].ToUpperInvariant();
                var number = flightNumber[2..];
                query.Add($"airline={Uri.EscapeDataString(airline)}");
                query.Add($"flight_number={Uri.EscapeDataString(number)}");
            }

            return $"schedules/{start}/{end}?{string.Join("&", query)}";
        }

        /// <summary>
        /// Whether a flight number can actually be pushed down to AeroAPI as an
        /// <c>airline</c>+<c>flight_number</c> pair — it needs at least the 2-character airline
        /// prefix. Shared by the unfiltered-query guard and <see cref="BuildUrl"/> so the guard can
        /// never accept a value the URL builder then silently ignores.
        /// </summary>
        private static bool HasUsableFlightNumber(string? flightNumber) =>
            !string.IsNullOrWhiteSpace(flightNumber) && flightNumber.Trim().Length >= 2;

        private static string Describe(FlightSearchCriteria criteria)
        {
            var route = !string.IsNullOrWhiteSpace(criteria.DepartureIata) || !string.IsNullOrWhiteSpace(criteria.ArrivalIata)
                ? $"{criteria.DepartureIata ?? "*"}->{criteria.ArrivalIata ?? "*"}"
                : null;
            var parts = new[] { route, criteria.FlightNumber, criteria.Date.ToString("yyyy-MM-dd") }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            return string.Join(" ", parts);
        }

        /// <summary>Parses an AeroAPI <c>...Z</c> timestamp, which is already UTC — no timezone
        /// conversion needed.</summary>
        private static DateTime? TryGetUtcDateTime(JsonElement el, string field)
        {
            if (!el.TryGetProperty(field, out var val) || val.ValueKind != JsonValueKind.String) return null;
            return DateTimeOffset.TryParse(
                val.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto)
                    ? DateTime.SpecifyKind(dto.UtcDateTime, DateTimeKind.Utc)
                    : null;
        }

        private static string? TryGetString(JsonElement el, string field)
        {
            if (!el.TryGetProperty(field, out var val) || val.ValueKind != JsonValueKind.String) return null;
            return val.GetString();
        }
    }
}
