using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroDataBox
{
    /// <summary>
    /// Searches flights between two airports on a given local service date via AeroDataBox's
    /// free-tier airport departures endpoint (#422) — a $0 substitute for AeroAPI's per-search
    /// billed <c>/schedules</c> call, for exactly the route-search shape (both airports known, no
    /// flight number). Flight-number search deliberately stays on AeroAPI: AeroDataBox resolves a
    /// marketing number to its operator (measured: <c>GET /flights/number/6E4120/…</c> comes back
    /// as <c>TK 1028</c>), and routing flight-number searches here would change which flight a
    /// traveler ends up tracking.
    ///
    /// Endpoint: <c>GET /flights/airports/iata/{iata}/{fromLocal}/{toLocal}?withLeg=true&amp;
    /// direction=Departure&amp;withCancelled=false&amp;withCargo=false&amp;withPrivate=false&amp;
    /// withCodeshared={IncludeCodeshares}</c>.
    ///
    /// <para>
    /// <b>No timezone lookup, unlike AeroAPI.</b> Every row carries both UTC and local times, the
    /// local one with its UTC offset embedded directly (e.g. <c>+03:00</c> — no separate IANA zone
    /// name is ever read), so this needs none of <c>AeroApiAirportTimezoneLookup</c>'s machinery
    /// and makes no separate <c>/airports/{id}</c> call. That matters for latency, not cost: this
    /// path costs nothing because it runs on AeroDataBox's free plan, not because of what it skips
    /// (#422 final review, finding 11 — on this feature's own recorded prices, the timezone lookup
    /// this avoids is a minority of an AeroAPI search's cost, and it is cached 365 days anyway).
    /// </para>
    ///
    /// <para>
    /// <b>The origin comes from the search criteria, not from the row (#422 final review, finding
    /// 1).</b> A live measurement on 2026-09-17 found <c>departure.airport</c> absent from all 36
    /// rows of a real departures response — the endpoint is anchored on the airport being queried,
    /// so this is true by construction, not an assumption. If a row ever DOES carry
    /// <c>departure.airport.iata</c> and it disagrees with the requested origin, that row is
    /// dropped as a defensive measure — the same bucket as a destination or date mismatch, not a
    /// missing-field failure. A row missing a field the mapping genuinely needs (the flight number,
    /// either the <c>departure</c> or <c>arrival</c> section, the arrival airport, or either
    /// scheduled time) is "structurally unmappable": if a window's <c>departures</c> array is
    /// non-empty but not one row is structurally mappable, the whole search fails loud — 502 with
    /// an Error log naming the row count — rather than being cached as an empty "no data" result,
    /// because that is precisely the silent-failure shape a wrong guess about the payload would
    /// otherwise take. When only some rows are unmappable, the rest are kept and a Warning names
    /// the count.
    /// </para>
    ///
    /// <para>
    /// <b>Two ≤12-hour windows per search, not one whole day (#422 fix round 2).</b>
    /// AeroDataBox's own documentation for this endpoint's end-of-range parameter reads "Should be
    /// more than beginning of the search range, but not more than by 12 hours" — a documented limit
    /// this repository already hit once, in #267, which quotes that same documentation. The removed
    /// provider logged nothing on failure, so the HTTP 400 #267 names was inferred from the
    /// documentation, never actually observed. So a requested local day is queried as two
    /// consecutive windows, <c>T00:00</c>–<c>T11:59</c> and <c>T12:00</c>–<c>T23:59</c> (see
    /// <see cref="DayWindows"/>), sent <i>sequentially</i> — a free RapidAPI plan's per-second limit
    /// is unmeasured, and two concurrent requests are the likeliest way to trip it — then merged and
    /// deduped by <c>(FlightNumber, ScheduledDepartureUtc)</c>. If either window fails, the whole
    /// search fails with that window's mapped <see cref="DomainException"/> and the other window is
    /// never sent: one window's rows are never returned as if they were the full day, because a
    /// silent half-day answer is exactly the kind of failure that hid #267 for its entire life.
    /// </para>
    ///
    /// <para>
    /// <b>The client-side keeper test is the row's own SCHEDULED local date, never the revised one.</b>
    /// <see cref="FlightSearchResult.ServiceDate"/> is the origin-local date of the SCHEDULED
    /// departure system-wide (CLAUDE.md), and <c>AeroApiFlightStatusProvider</c> matches its status
    /// syncs against <c>scheduled_out</c> converted to origin-local — never a revised or estimated
    /// time. Filtering here on revised time would reassign a delayed flight to a service date its own
    /// status syncs can never match: a flight scheduled late one day and revised past local midnight
    /// would be stamped with the next day's <c>ServiceDate</c> while its own <c>scheduled_out</c>
    /// still falls on the day before, so every later status sync would compare the wrong date and the
    /// tracked flight would silently stop updating. Filtering on the scheduled date instead makes
    /// <c>ToResult</c>'s <c>ServiceDate: criteria.Date</c> true by construction, not by assumption —
    /// every row this method keeps has its own scheduled local date equal to the requested date. The
    /// accepted residual cost: a flight delayed past local midnight — its revised time past the end
    /// of the LAST window this provider ever queries — is not returned at all, because it never
    /// appears in either window's response for this provider to filter in the first place.
    /// Recovering it would take a third window into the next local day, which would raise this
    /// feature's per-search call count again; that is deliberately not done.
    /// </para>
    ///
    /// <para>
    /// <b>No destination filter server-side.</b> The endpoint is anchored on one airport only, so
    /// every row for every destination it serves comes back; this filters to the requested
    /// <see cref="FlightSearchCriteria.ArrivalIata"/> client-side, exactly as the removed provider
    /// did.
    /// </para>
    ///
    /// <para>
    /// <b>Codeshares pass straight through.</b> <c>withCodeshared</c> maps directly onto
    /// <see cref="FlightSearchCriteria.IncludeCodeshares"/> (measured at FRA, one hour: 42 rows with
    /// it off, 158 with it on — all 116 extra rows codeshares) and is never filtered client-side.
    /// </para>
    /// </summary>
    public sealed class AeroDataBoxFlightSearchProvider : IFlightSearchProvider
    {
        /// <summary>The named-client key this provider's <see cref="HttpClient"/> is registered
        /// under — the same key the DI registration and this provider share so a caller resolves
        /// the client this provider actually expects, rather than the name being a bare string
        /// literal repeated at every call site (precedent:
        /// <c>OurAirportsVerificationSource.HttpClientName</c>).</summary>
        public const string HttpClientName = "AeroDataBox";

        private const string Operation = "Flight search";

        // 500 Internal Server Error — our key or RapidAPI plan is the problem (401/403 from
        // AeroDataBox), or an unclassified 4xx that cannot be told apart between our own request
        // being malformed and the caller's input being well-formed but unknown to AeroDataBox (see
        // ReadOrThrowAsync) — either way, not something the caller can retry their way out of.
        private const int StatusCode500InternalError = 500;

        // 502 Bad Gateway — AeroDataBox was unreachable, timed out, returned an unparseable body, or
        // an unclassified (non-4xx) error status.
        private const int StatusCode502BadGateway = 502;

        // 503 Service Unavailable — AeroDataBox throttled us (429). The client may retry later.
        private const int StatusCode503ServiceUnavailable = 503;

        // Whether AeroDataBox's window bounds are inclusive at the 11:59/12:00 boundary is
        // unmeasured — the single-window design already assumed inclusive minute bounds at 23:59,
        // and this split simply applies that same assumption at one more boundary. The dedupe step
        // in SearchAsync is the safety net if a flight straddling that boundary comes back from both
        // windows.
        private static readonly (string FromTime, string ToTime)[] DayWindows =
        [
            ("00:00", "11:59"),
            ("12:00", "23:59"),
        ];

        // Catches credential-shaped fields in an upstream body even though the RapidAPI key travels
        // only in the X-RapidAPI-Key request header and is never expected to appear in a response
        // body — the same defense-in-depth AeroApiResponse applies for AeroAPI.
        private static readonly Regex KeyLikeFieldPattern = new(
            @"(""?(?:x-)?(?:rapidapi[_-]?key|api[_-]?key|access[_-]?token|secret)""?\s*[:=]\s*)""?[^""\s,}]+""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly HttpClient _http;
        private readonly ILogger<AeroDataBoxFlightSearchProvider> _logger;
        private readonly string? _apiKey;

        public AeroDataBoxFlightSearchProvider(
            HttpClient http,
            IOptions<AeroDataBoxOptions> options,
            ILogger<AeroDataBoxFlightSearchProvider> logger)
        {
            _http = http;
            _logger = logger;
            _apiKey = options.Value.ApiKey;
        }

        public async Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(criteria.DepartureIata) || string.IsNullOrWhiteSpace(criteria.ArrivalIata))
            {
                // Once Task 2's selector is wired in, only a routing bug can reach this provider
                // without both airports — a flight-number search is never routed here. That makes
                // this our defect, not the caller's, so it is a 500 with an operator-facing
                // DiagnosticDetail naming the broken invariant, not a 400 that would blame the
                // caller for a search shape they never sent (#422 fix round 2).
                _logger.LogError(
                    "AeroDataBoxFlightSearchProvider reached without both a departure and an arrival airport for {Criteria} — a route-search selector bug, not a caller error",
                    Describe(criteria));
                throw new DomainException(
                    "Flight data is unavailable.",
                    StatusCode500InternalError,
                    "AeroDataBoxFlightSearchProvider was invoked without both a departure and an arrival airport — a route search reached this provider without both, which is a selector routing bug.");
            }

            var rows = new List<Row>();
            foreach (var (fromTime, toTime) in DayWindows)
            {
                var url = BuildUrl(criteria, fromTime, toTime);
                rows.AddRange(await SearchWindowAsync(url, criteria, ct));
            }

            // Dedupe by (FlightNumber, ScheduledDepartureUtc) — the safety net described above for
            // an overlap at the window boundary. The key includes the flight number, so a codeshare
            // and its operator sharing the same scheduled departure both survive.
            return rows
                .GroupBy(r => (r.FlightNumber, r.ScheduledOutUtc))
                .Select(g => g.First())
                .Select(r => ToResult(r, criteria.Date))
                .OrderBy(r => r.ScheduledDepartureUtc)
                .ThenBy(r => r.FlightNumber, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Issues one ≤12-hour window request and maps its rows, or throws a
        /// <see cref="DomainException"/> for any provider-side failure — including a client-side
        /// timeout, which is this client's only timeout, since it carries no resilience handler.
        /// </summary>
        private async Task<List<Row>> SearchWindowAsync(string url, FlightSearchCriteria criteria, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url, ct);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // HttpClient.Timeout throws TaskCanceledException, which IS an
                // OperationCanceledException. This client is forbidden a resilience handler by the
                // global constraints, so HttpClient.Timeout (from AeroDataBoxOptions.TimeoutSeconds)
                // is its ONLY timeout — making this the most likely real-world "unreachable" case.
                // Only genuine caller cancellation (ct itself signalled) is allowed to propagate as
                // an OperationCanceledException; anything else maps to 502 (#422 fix round 2), with
                // the exception logged and carried as the DomainException's inner exception, the
                // same as the unreachable-host branch below (#422 fix round 3). This provider does
                // not own the client's timeout — Task 2 sets it in DI — so the DiagnosticDetail
                // names no number of seconds.
                _logger.LogError(
                    ex, "AeroDataBox flight search request timed out for {Criteria}", Describe(criteria));
                throw new DomainException(
                    $"{Operation} is temporarily unavailable. Please try again shortly.",
                    ex,
                    StatusCode502BadGateway,
                    "AeroDataBox did not respond before the client timeout.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AeroDataBox flight search request failed for {Criteria}", Describe(criteria));
                throw new DomainException(
                    $"{Operation} is temporarily unavailable. Please try again shortly.",
                    ex,
                    StatusCode502BadGateway);
            }

            var criteriaDescription = Describe(criteria);
            using var doc = await ReadOrThrowAsync(response, criteriaDescription, ct);

            // A 204 is the only success status read as "no data" (see ReadOrThrowAsync) — doc is
            // null only for that case, and there is nothing further to parse.
            if (doc is null) return [];

            // #422 final review, finding 6: the measured "no data" signal is a 204. A 200 body that
            // is not an object, or that has no "departures" array, is an unrecognised response
            // shape — not "zero flights" — so it is loud (502), exactly like the empty/unparseable
            // body case ReadOrThrowAsync already handles.
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("departures", out var departures) ||
                departures.ValueKind != JsonValueKind.Array)
            {
                _logger.LogError(
                    "AeroDataBox {Operation} returned a 200 response for {Criteria} whose body is not a JSON object with a \"departures\" array",
                    Operation, criteriaDescription);
                throw new DomainException(
                    $"{Operation} is temporarily unavailable. Please try again shortly.",
                    StatusCode502BadGateway,
                    "AeroDataBox returned HTTP 200 with a body that is not an object carrying a \"departures\" array.");
            }

            var rows = new List<Row>();
            var totalRows = 0;
            var unmappableCount = 0;
            var mappableCount = 0;

            // Non-null by construction: SearchAsync's guard already rejected a missing
            // DepartureIata/ArrivalIata before any window request was ever built, matching the
            // existing `criteria.DepartureIata!.Trim()` in BuildUrl.
            var requestedOriginIata = criteria.DepartureIata!.Trim();
            var requestedArrivalIata = criteria.ArrivalIata!.Trim();

            foreach (var element in departures.EnumerateArray())
            {
                totalRows++;

                // A non-object array element (null, a scalar, ...) is structurally unmappable in
                // the same sense as an object missing a required field — it counts toward
                // unmappableCount rather than being skipped before totalRows is even incremented,
                // otherwise a departures array containing only non-object elements never sets
                // totalRows > 0 and the all-unmappable guard below never engages.
                if (element.ValueKind != JsonValueKind.Object)
                {
                    unmappableCount++;
                    continue;
                }

                var (row, isUnmappable) = TryMapRow(element, requestedOriginIata, requestedArrivalIata, criteria.Date);
                if (isUnmappable)
                {
                    unmappableCount++;
                    continue;
                }

                // Structurally fine either way — kept, or filtered out by destination/date/origin,
                // which is a legitimate "not this row", not a mapping failure.
                mappableCount++;
                if (row is not null) rows.Add(row);
            }

            // #422 final review, finding 1: "structurally unmappable" means a row is missing a
            // field the mapping needs — never that a destination or date filter excluded it. A
            // non-empty departures array with not one structurally mappable row is a shape change,
            // and must be loud rather than cached as an empty "no data" answer.
            if (totalRows > 0 && mappableCount == 0)
            {
                _logger.LogError(
                    "AeroDataBox {Operation} returned {RowCount} departures rows for {Criteria}, but none were structurally mappable — every row was missing a field the mapping requires",
                    Operation, totalRows, criteriaDescription);
                throw new DomainException(
                    $"{Operation} is temporarily unavailable. Please try again shortly.",
                    StatusCode502BadGateway,
                    $"AeroDataBox returned {totalRows} departures rows for {criteriaDescription}; none were structurally mappable (all missing a required field).");
            }

            if (unmappableCount > 0)
            {
                _logger.LogWarning(
                    "AeroDataBox {Operation} returned {UnmappableCount} of {RowCount} departures rows for {Criteria} that were structurally unmappable — skipped, not treated as a shape failure since {MappableCount} other rows mapped fine",
                    Operation, unmappableCount, totalRows, criteriaDescription, mappableCount);
            }

            return rows;
        }

        /// <summary>
        /// Builds one ≤12-hour departures query for <see cref="FlightSearchCriteria.DepartureIata"/>
        /// and the given local time-of-day bounds (see <see cref="DayWindows"/>). AeroDataBox takes
        /// the window as plain local wall-clock strings (no offset, no UTC conversion) — it resolves
        /// the airport's own zone server-side. The date is formatted with
        /// <see cref="CultureInfo.InvariantCulture"/> — <c>{criteria.Date:yyyy-MM-dd}</c> would use
        /// whatever calendar the running culture defaults to (Thai renders this year as
        /// <c>2569</c>), which AeroDataBox would not parse as the intended Gregorian date.
        /// </summary>
        private static string BuildUrl(FlightSearchCriteria criteria, string fromTime, string toTime)
        {
            var date = criteria.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var from = $"{date}T{fromTime}";
            var to = $"{date}T{toTime}";
            var origin = criteria.DepartureIata!.Trim().ToUpperInvariant();

            var query = string.Join(
                "&",
                "withLeg=true",
                "direction=Departure",
                "withCancelled=false",
                "withCargo=false",
                "withPrivate=false",
                $"withCodeshared={(criteria.IncludeCodeshares ? "true" : "false")}");

            return $"flights/airports/iata/{Uri.EscapeDataString(origin)}/{from}/{to}?{query}";
        }

        /// <summary>A departures row exactly as AeroDataBox returns it, before mapping to the public shape.</summary>
        private sealed record Row(
            string FlightNumber,
            string OriginIata,
            string DestinationIata,
            DateTime ScheduledOutUtc,
            DateTime ScheduledInUtc);

        private static FlightSearchResult ToResult(Row row, DateOnly serviceDate) =>
            new(
                FlightNumber: row.FlightNumber,
                ServiceDate: serviceDate,
                DepartureAirportIata: row.OriginIata.ToUpperInvariant(),
                ArrivalAirportIata: row.DestinationIata.ToUpperInvariant(),
                ScheduledDepartureUtc: row.ScheduledOutUtc,
                ScheduledArrivalUtc: row.ScheduledInUtc);

        /// <summary>
        /// Maps one raw departures-array element against the requested origin/arrival/date, or
        /// returns a null <c>Row</c> to skip it — either because it is <b>structurally
        /// unmappable</b> (missing a field the mapping needs: the flight number, the
        /// <c>departure</c>/<c>arrival</c> section, the arrival airport, or either scheduled time —
        /// see <c>IsStructurallyUnmappable</c> in the return tuple, which the caller uses to decide
        /// whether a whole window has failed, per the class remarks) or because it is structurally
        /// fine but does not belong in the result: its own departure airport disagrees with the one
        /// requested (defensive; no real row has ever carried one — see the class remarks), it is
        /// not for the requested arrival airport (AeroDataBox has no destination filter
        /// server-side, so this is applied here), or its own scheduled local departure date is not
        /// the requested date (see the class remarks for why the revised time plays no part in this
        /// filter).
        /// </summary>
        private static (Row? Row, bool IsStructurallyUnmappable) TryMapRow(
            JsonElement element, string requestedOriginIata, string requestedArrivalIata, DateOnly requestedDate)
        {
            var numberRaw = TryGetString(element, "number");
            if (string.IsNullOrWhiteSpace(numberRaw)) return (null, true);

            if (!TryGetSection(element, "departure", out var departure)) return (null, true);
            if (!TryGetSection(element, "arrival", out var arrival)) return (null, true);

            // The origin comes from the requested criteria (#422 final review, finding 1) — the
            // endpoint is anchored on it, and a live measurement found departure.airport absent
            // from every real row. If a row DOES carry one and it disagrees, that is not a
            // missing-field problem; it is dropped the same way a destination/date mismatch is.
            var ownOriginIata = TryGetNestedString(departure, "airport", "iata");
            if (!string.IsNullOrWhiteSpace(ownOriginIata) &&
                !ownOriginIata.Trim().Equals(requestedOriginIata, StringComparison.OrdinalIgnoreCase))
            {
                return (null, false);
            }

            var destinationIata = TryGetNestedString(arrival, "airport", "iata");
            if (string.IsNullOrWhiteSpace(destinationIata)) return (null, true);

            var scheduledOutUtc = TryGetTimeUtc(departure, "scheduledTime");
            var scheduledInUtc = TryGetTimeUtc(arrival, "scheduledTime");
            if (scheduledOutUtc is null || scheduledInUtc is null) return (null, true);

            // Keep on the row's own SCHEDULED local date — the revised time plays no part in this
            // filter. ServiceDate is the origin-local date of the SCHEDULED departure system-wide
            // (CLAUDE.md), and AeroApiFlightStatusProvider matches its status syncs against
            // scheduled_out converted to origin-local, never a revised or estimated time. Filtering
            // here on revised time would reassign a delayed flight to a service date its own status
            // syncs can never match — see the class remarks. This also makes ToResult's
            // `ServiceDate: criteria.Date` true by construction: every row this method returns has
            // its own scheduled local date equal to requestedDate. A row whose local date cannot be
            // determined at all is structurally unmappable — ServiceDate has nothing to be built
            // from — never a silent "not this date" filter.
            var scheduledLocalDate = TryGetLocalDate(departure, "scheduledTime");
            if (scheduledLocalDate is null) return (null, true);

            // The arrival, compared only here, needs the same trim/case-insensitive treatment as
            // the origin above so a padded FlightSearchCriteria.ArrivalIata does not silently
            // return zero rows (#422 fix round 2).
            if (!destinationIata.Trim().Equals(requestedArrivalIata, StringComparison.OrdinalIgnoreCase))
                return (null, false);

            if (scheduledLocalDate.Value != requestedDate) return (null, false);

            // AeroDataBox's "number" is space-separated ("TK 1028"); every other provider and the
            // domain's own FlightNumberFormat use no space ("TK1028").
            var flightNumber = numberRaw.Replace(" ", "").ToUpperInvariant();

            return (new Row(flightNumber, requestedOriginIata, destinationIata, scheduledOutUtc.Value, scheduledInUtc.Value), false);
        }

        private static bool TryGetSection(JsonElement el, string field, out JsonElement section)
        {
            if (el.TryGetProperty(field, out section) && section.ValueKind == JsonValueKind.Object) return true;
            section = default;
            return false;
        }

        private static string? TryGetNestedString(JsonElement el, string section, string field)
        {
            if (!el.TryGetProperty(section, out var sec) || sec.ValueKind != JsonValueKind.Object) return null;
            return TryGetString(sec, field);
        }

        private static string? TryGetString(JsonElement el, string field)
        {
            if (!el.TryGetProperty(field, out var val) || val.ValueKind != JsonValueKind.String) return null;
            return val.GetString();
        }

        /// <summary>Parses a <c>{ "utc": "...", "local": "..." }</c> time object's <c>utc</c> member.</summary>
        private static DateTime? TryGetTimeUtc(JsonElement section, string timeField)
        {
            if (!section.TryGetProperty(timeField, out var time) || time.ValueKind != JsonValueKind.Object) return null;
            var raw = TryGetString(time, "utc");
            if (raw is null) return null;
            return DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto)
                    ? DateTime.SpecifyKind(dto.UtcDateTime, DateTimeKind.Utc)
                    : null;
        }

        /// <summary>Parses a <c>{ "utc": "...", "local": "..." }</c> time object's <c>local</c>
        /// member into just its calendar date — the offset is embedded in the string itself, so no
        /// separate zone lookup is needed.</summary>
        private static DateOnly? TryGetLocalDate(JsonElement section, string timeField)
        {
            if (!section.TryGetProperty(timeField, out var time) || time.ValueKind != JsonValueKind.Object) return null;
            var raw = TryGetString(time, "local");
            if (raw is null) return null;
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
                ? DateOnly.FromDateTime(dto.DateTime)
                : null;
        }

        private static string Describe(FlightSearchCriteria criteria) =>
            $"{criteria.DepartureIata}->{criteria.ArrivalIata} {criteria.Date:yyyy-MM-dd}";

        /// <summary>
        /// Reads an AeroDataBox response, throwing <see cref="DomainException"/> for any
        /// provider-side failure, or returning <c>null</c> for a successful response with no data
        /// to report — <c>204 No Content</c> only, now measured (2026-09-17, #422 final review) as
        /// what AeroDataBox actually sends both for a quiet window and for an unknown airport. An
        /// empty or whitespace body on any OTHER success status is deliberately NOT treated as "no
        /// data" (#422 fix round 3): it maps to 502, logged like any other unparseable body, because
        /// letting one window silently answer "empty" while the other returns rows would present
        /// half a day as a full day — exactly what the partial-failure rule above exists to forbid.
        /// A non-null return here is a parsed <see cref="JsonDocument"/> of unknown shape — the
        /// caller (<see cref="SearchWindowAsync"/>) is what rejects a body that parses but is not an
        /// object carrying a <c>departures</c> array, since that is a mapping-shape concern, not a
        /// transport one. Follows <c>AeroApiResponse.ReadOrThrowAsync</c>'s shape without reusing
        /// that type, since its messages name AeroAPI specifically. Three output status buckets from
        /// four input cases (#422 fix round 2): 401/403 (our credentials or plan) → 500; 429
        /// (throttled) → 503 with Retry-After; any other 4xx → 500 — this cannot be told apart
        /// between our own request being malformed (#267's 12-hour cap is the closest example, and
        /// even that 400 was only ever inferred from AeroDataBox's own documentation, never
        /// observed — the removed provider logged nothing) and well-formed caller input AeroDataBox
        /// happens not to recognise, which for an unknown-but-well-formed airport is now measured as
        /// a 204, not a 4xx — only an out-of-range date remains unmeasured; 5xx, unreachable,
        /// timed-out, unparseable, or any other non-4xx status (a 3xx included, since this client's
        /// redirects are disabled at the transport — see DI) → 502.
        /// </summary>
        private async Task<JsonDocument?> ReadOrThrowAsync(HttpResponseMessage response, string criteria, CancellationToken ct)
        {
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NoContent) return null;

                var body = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(body))
                {
                    // Reverses #422 fix round 2: an empty/whitespace 200 body is not valid JSON, so it
                    // is not "no data" either — see the class remarks and this method's summary
                    // (#422 fix round 3). Logged and mapped exactly like the JsonException case just
                    // below.
                    _logger.LogError(
                        "AeroDataBox returned an empty {Operation} response body ({StatusCode}) for {Criteria}",
                        Operation, status, criteria);
                    throw new DomainException(
                        $"{Operation} is temporarily unavailable. Please try again shortly.",
                        StatusCode502BadGateway,
                        $"AeroDataBox returned HTTP {status} with an empty or whitespace body.");
                }

                try
                {
                    return JsonDocument.Parse(body);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex,
                        "AeroDataBox returned an unparseable {Operation} response ({StatusCode}) for {Criteria}",
                        Operation, status, criteria);
                    throw new DomainException(
                        $"{Operation} is temporarily unavailable. Please try again shortly.",
                        StatusCode502BadGateway,
                        $"AeroDataBox returned HTTP {status} with a body that is not valid JSON.");
                }
            }

            var upstreamBody = Redact(await SafeReadBodyAsync(response, ct));

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    _logger.LogError(
                        "AeroDataBox rejected our credentials on {Operation} ({StatusCode}) for {Criteria}: {Body}",
                        Operation, status, criteria, upstreamBody);
                    throw new DomainException(
                        "Flight data is unavailable.",
                        StatusCode500InternalError,
                        $"AeroDataBox returned HTTP {status}: {upstreamBody}");

                case HttpStatusCode.TooManyRequests:
                    var retryAfter = DescribeRetryAfter(response);
                    _logger.LogWarning(
                        "AeroDataBox throttled {Operation} ({StatusCode}) for {Criteria}{RetryAfterSuffix}",
                        Operation, status, criteria, retryAfter is null ? "" : $" (Retry-After: {retryAfter})");
                    throw new DomainException(
                        retryAfter is null
                            ? $"{Operation} is busy. Please try again shortly."
                            : $"{Operation} is busy. Please try again shortly. Retry-After: {retryAfter}.",
                        StatusCode503ServiceUnavailable,
                        $"AeroDataBox returned HTTP 429: {upstreamBody}");

                default:
                    if (status is >= 400 and < 500)
                    {
                        // A 4xx here cannot be told apart between two causes (#422 fix round 3):
                        // either our own request is malformed — #267's 12-hour cap is the closest
                        // example, though even that 400 was never actually observed: the removed
                        // provider logged nothing, so #267 only ever quotes AeroDataBox's own
                        // parameter documentation, not a recorded response — or the caller's input
                        // is well-formed but unknown to AeroDataBox. For an unknown-but-well-formed
                        // airport this is now measured (2026-09-17, #422 final review): AeroDataBox
                        // answers 204, not a 4xx, so this branch would only ever be reached today by
                        // an out-of-range date, which remains unmeasured, or a genuine defect of
                        // ours. It maps to 500 and logs at Error deliberately, so that a systematic
                        // defect stays loud: #267's inferred 400 was silent for that provider's
                        // entire life. The accepted cost: a 4xx caused by the caller's own input
                        // also surfaces as a 500 with an error log, rather than the 400 that input
                        // problem would otherwise deserve. The client-facing message deliberately
                        // does not say "try again" — that would invite retrying a request that, if
                        // the cause really is ours, fails identically forever. DiagnosticDetail
                        // still carries the upstream status and the redacted body.
                        _logger.LogError(
                            "AeroDataBox {Operation} rejected our request ({StatusCode}) for {Criteria}: {Body}",
                            Operation, status, criteria, upstreamBody);
                        throw new DomainException(
                            "Flight data is unavailable.",
                            StatusCode500InternalError,
                            $"AeroDataBox returned HTTP {status}: {upstreamBody}");
                    }

                    _logger.LogError(
                        "AeroDataBox {Operation} returned an unexpected status ({StatusCode}) for {Criteria}: {Body}",
                        Operation, status, criteria, upstreamBody);
                    throw new DomainException(
                        $"{Operation} is temporarily unavailable. Please try again shortly.",
                        StatusCode502BadGateway,
                        $"AeroDataBox returned HTTP {status}: {upstreamBody}");
            }
        }

        private static string? DescribeRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter is null) return null;

            if (retryAfter.Delta is { } delta)
                return $"{Math.Max(0, (int)delta.TotalSeconds)}s";

            if (retryAfter.Date is { } date)
                return date.ToString("O");

            return null;
        }

        /// <summary>
        /// Strips the configured API key — and anything shaped like a credential field — out of
        /// upstream text before it reaches <see cref="DomainException.DiagnosticDetail"/> or a log.
        /// The RapidAPI key travels only in the <c>X-RapidAPI-Key</c> request header and is never
        /// expected to appear in a response body; this exists purely as defense in depth.
        /// </summary>
        private string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var redacted = text;

            if (!string.IsNullOrEmpty(_apiKey))
                redacted = redacted.Replace(_apiKey, "[REDACTED]", StringComparison.OrdinalIgnoreCase);

            redacted = KeyLikeFieldPattern.Replace(redacted, "$1[REDACTED]");

            return redacted;
        }

        private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception)
            {
                return "(no body)";
            }
        }
    }
}
