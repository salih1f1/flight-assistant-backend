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
    /// <b>No timezone lookup, unlike AeroAPI.</b> Every row carries both UTC and local times with
    /// the IANA zone already resolved server-side, so this needs none of
    /// <c>AeroApiAirportTimezoneLookup</c>'s machinery and makes no separate <c>/airports/{id}</c>
    /// call — most of why this path costs nothing.
    /// </para>
    ///
    /// <para>
    /// <b>Two ≤12-hour windows per search, not one whole day (fix round 2, finding 1).</b>
    /// AeroDataBox's own documentation for this endpoint's end-of-range parameter reads "Should be
    /// more than beginning of the search range, but not more than by 12 hours" — a limit this
    /// repository already hit once, in #267: commit <c>f89f95e</c> records that a single
    /// <c>T00:00</c>–<c>T23:59</c> window "caused every flight-search request to fail with HTTP
    /// 400". So a requested local day is queried as two consecutive windows,
    /// <c>T00:00</c>–<c>T11:59</c> and <c>T12:00</c>–<c>T23:59</c> (see <see cref="DayWindows"/>),
    /// sent <i>sequentially</i> — a free RapidAPI plan's per-second limit is unmeasured, and two
    /// concurrent requests are the likeliest way to trip it — then merged and deduped by
    /// <c>(FlightNumber, ScheduledDepartureUtc)</c>. If either window fails, the whole search fails
    /// with that window's mapped <see cref="DomainException"/>: one window's rows are never
    /// returned as if they were the full day, because a silent half-day answer is exactly the kind
    /// of failure that hid #267 for its entire life.
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
        /// <summary>Named <see cref="HttpClient"/> this provider is registered against. Nothing in
        /// this task uses it — Task 2's selector/DI wiring does — but it exists here (precedent:
        /// <c>OurAirportsVerificationSource.HttpClientName</c>) so that name is not a bare string
        /// literal repeated at every call site.</summary>
        public const string HttpClientName = "AeroDataBox";

        private const string Operation = "Flight search";

        // 500 Internal Server Error — our key or RapidAPI plan is the problem (401/403 from
        // AeroDataBox), or the request THIS PROVIDER built was rejected (any other 4xx) — either
        // way, not something the caller can retry their way out of.
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
                // caller for a search shape they never sent (fix round 2, finding 9).
                _logger.LogError(
                    "AeroDataBoxFlightSearchProvider reached without both a departure and an arrival airport for {Criteria} — a route-search selector bug, not a caller error",
                    Describe(criteria));
                throw new DomainException(
                    "Flight data is temporarily unavailable.",
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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient.Timeout throws TaskCanceledException, which IS an
                // OperationCanceledException. This client is forbidden a resilience handler by the
                // global constraints, so HttpClient.Timeout (from AeroDataBoxOptions.TimeoutSeconds)
                // is its ONLY timeout — making this the most likely real-world "unreachable" case.
                // Only genuine caller cancellation (ct itself signalled) is allowed to propagate as
                // an OperationCanceledException; anything else maps to 502 (fix round 2, finding 2).
                _logger.LogError(
                    "AeroDataBox flight search request timed out for {Criteria}", Describe(criteria));
                throw new DomainException(
                    $"{Operation} is temporarily unavailable. Please try again shortly.",
                    StatusCode502BadGateway);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AeroDataBox flight search request failed for {Criteria}", Describe(criteria));
                throw new DomainException(
                    $"{Operation} is temporarily unavailable. Please try again shortly.",
                    ex,
                    StatusCode502BadGateway);
            }

            using var doc = await ReadOrThrowAsync(response, Describe(criteria), ct);

            var rows = new List<Row>();
            if (doc is not null &&
                doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("departures", out var departures) &&
                departures.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in departures.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;
                    // Non-null by construction: SearchAsync's guard already rejected a missing
                    // ArrivalIata before any window request was ever built, matching the existing
                    // `criteria.DepartureIata!.Trim()` in BuildUrl.
                    var row = TryMapRow(element, criteria.ArrivalIata!, criteria.Date);
                    if (row is not null) rows.Add(row);
                }
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
        /// Maps one raw departures-array element, or returns null to skip it silently — either
        /// because it is missing something required to place it, because it is not for the
        /// requested arrival airport (AeroDataBox has no destination filter server-side, so this is
        /// applied here), or because its own scheduled local departure date is not the requested
        /// date (see the class remarks for why the revised time plays no part in this filter).
        /// </summary>
        private static Row? TryMapRow(JsonElement element, string requestedArrivalIata, DateOnly requestedDate)
        {
            var numberRaw = TryGetString(element, "number");
            if (string.IsNullOrWhiteSpace(numberRaw)) return null;

            if (!TryGetSection(element, "departure", out var departure)) return null;
            if (!TryGetSection(element, "arrival", out var arrival)) return null;

            var originIata = TryGetNestedString(departure, "airport", "iata");
            var destinationIata = TryGetNestedString(arrival, "airport", "iata");
            if (string.IsNullOrWhiteSpace(originIata) || string.IsNullOrWhiteSpace(destinationIata)) return null;

            // The origin is trimmed (and uppercased) in BuildUrl already; the arrival, compared only
            // here, needs the same trim so a padded FlightSearchCriteria.ArrivalIata does not
            // silently return zero rows (fix round 2, finding 13).
            if (!destinationIata.Trim().Equals(requestedArrivalIata.Trim(), StringComparison.OrdinalIgnoreCase)) return null;

            var scheduledOutUtc = TryGetTimeUtc(departure, "scheduledTime");
            var scheduledInUtc = TryGetTimeUtc(arrival, "scheduledTime");
            if (scheduledOutUtc is null || scheduledInUtc is null) return null;

            // Keep on the row's own SCHEDULED local date — the revised time plays no part in this
            // filter. ServiceDate is the origin-local date of the SCHEDULED departure system-wide
            // (CLAUDE.md), and AeroApiFlightStatusProvider matches its status syncs against
            // scheduled_out converted to origin-local, never a revised or estimated time. Filtering
            // here on revised time would reassign a delayed flight to a service date its own status
            // syncs can never match — see the class remarks. This also makes ToResult's
            // `ServiceDate: criteria.Date` true by construction: every row this method returns has
            // its own scheduled local date equal to requestedDate.
            var scheduledLocalDate = TryGetLocalDate(departure, "scheduledTime");
            if (scheduledLocalDate is null || scheduledLocalDate.Value != requestedDate) return null;

            // AeroDataBox's "number" is space-separated ("TK 1028"); every other provider and the
            // domain's own FlightNumberFormat use no space ("TK1028").
            var flightNumber = numberRaw.Replace(" ", "").ToUpperInvariant();

            return new Row(flightNumber, originIata, destinationIata, scheduledOutUtc.Value, scheduledInUtc.Value);
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
        /// provider-side failure, or returning <c>null</c> for a successful response with no usable
        /// body — <c>204 No Content</c>, or an empty/whitespace body on any other success status
        /// (defensive and unobserved: no spike ever hit an empty window, but the global constraint
        /// is "no data is an empty list, never an error"). Follows
        /// <c>AeroApiResponse.ReadOrThrowAsync</c>'s shape without reusing that type, since its
        /// messages name AeroAPI specifically. Four status buckets (fix round 2, finding/ruling B):
        /// 401/403 (our credentials or plan) → 500; 429 (throttled) → 503 with Retry-After; any
        /// other 4xx (a request THIS PROVIDER built wrong — #267 is a live example) → 500, with a
        /// client message that does not invite a retry; 5xx, unreachable, timed-out or unparseable
        /// → 502.
        /// </summary>
        private async Task<JsonDocument?> ReadOrThrowAsync(HttpResponseMessage response, string criteria, CancellationToken ct)
        {
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NoContent) return null;

                var body = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(body)) return null;

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
                        "Flight data is temporarily unavailable.",
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
                        // Any other 4xx means the request THIS PROVIDER built is wrong — our bug,
                        // not something a retry fixes. #267 is a live example: the removed
                        // whole-day window earned a 400 on every single call. The client-facing
                        // message deliberately does not say "try again" — that would invite
                        // retrying a request that fails identically forever, and point an operator
                        // at AeroDataBox instead of our own URL builder. DiagnosticDetail still
                        // carries the upstream status and the redacted body.
                        _logger.LogError(
                            "AeroDataBox {Operation} rejected our request ({StatusCode}) for {Criteria}: {Body}",
                            Operation, status, criteria, upstreamBody);
                        throw new DomainException(
                            "Flight data is temporarily unavailable.",
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
