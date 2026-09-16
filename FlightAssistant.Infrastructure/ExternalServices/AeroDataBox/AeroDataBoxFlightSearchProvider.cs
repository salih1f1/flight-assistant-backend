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
    /// <b>Why the query window is a whole local day, and why the keeper test is not.</b> AeroDataBox's
    /// own departures window filters each row on its <i>revised</i> departure time, not its scheduled
    /// one (measured, 2026-09-16 spike): a flight scheduled 09:35 local but revised to 10:01 was
    /// absent from a 09:00–10:00 query and present in 09:00–11:00. A narrow window would therefore
    /// silently drop delayed flights, so this provider always requests the origin's whole local day
    /// (<c>T00:00</c>–<c>T23:59</c>, as the removed pre-#269 AeroDataBox provider did) — wide enough
    /// that the server's revised-time filter cannot exclude a flight that still belongs to the
    /// requested day. The client-side filter this provider applies afterward is deliberately the
    /// opposite: it keeps a row on its own <i>scheduled</i> local date only, never its revised one.
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
    /// accepted residual cost: a flight delayed past local midnight, whose revised time falls outside
    /// the whole-day window entirely, is not returned at all — that flight never appears in the
    /// response for this provider to filter in the first place. Widening the query window further
    /// could recover it, but no spike has measured whether AeroDataBox accepts a window longer than 24
    /// hours, so that stays a deferred follow-up, not something this change attempts.
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
        private const string Operation = "Flight search";

        // 500 Internal Server Error — our key or RapidAPI plan is the problem (401/403 from
        // AeroDataBox), not something the caller can retry their way out of.
        private const int StatusCode500InternalError = 500;

        // 502 Bad Gateway — AeroDataBox was unreachable, returned an unparseable body, or an
        // unclassified error status.
        private const int StatusCode502BadGateway = 502;

        // 503 Service Unavailable — AeroDataBox throttled us (429). The client may retry later.
        private const int StatusCode503ServiceUnavailable = 503;

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
                _logger.LogWarning(
                    "Rejected an AeroDataBox search missing a departure or arrival airport for {Criteria}",
                    Describe(criteria));
                throw new DomainException(
                    "A route search must include both a departure and an arrival airport.", 400);
            }

            var url = BuildUrl(criteria);

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url, ct);
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
            if (doc.RootElement.TryGetProperty("departures", out var departures) &&
                departures.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in departures.EnumerateArray())
                {
                    var row = TryMapRow(element, criteria.ArrivalIata, criteria.Date);
                    if (row is not null) rows.Add(row);
                }
            }

            return rows
                .Select(r => ToResult(r, criteria.Date))
                .OrderBy(r => r.ScheduledDepartureUtc)
                .ThenBy(r => r.FlightNumber, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Builds the whole-local-day departures query for <see cref="FlightSearchCriteria.DepartureIata"/>.
        /// AeroDataBox takes the window as plain local wall-clock strings (no offset, no UTC
        /// conversion) — it resolves the airport's own zone server-side.
        /// </summary>
        private static string BuildUrl(FlightSearchCriteria criteria)
        {
            var from = $"{criteria.Date:yyyy-MM-dd}T00:00";
            var to = $"{criteria.Date:yyyy-MM-dd}T23:59";
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
        /// applied here), or because it no longer belongs to the requested local service date (the
        /// revised-time gotcha — see the class remarks).
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

            if (!destinationIata.Equals(requestedArrivalIata, StringComparison.OrdinalIgnoreCase)) return null;

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
        /// provider-side failure. Follows <c>AeroApiResponse.ReadOrThrowAsync</c>'s shape without
        /// reusing that type, since its messages name AeroAPI specifically. Unlike AeroAPI's
        /// version, a rejected-parameter status has no dedicated bucket here — it falls into
        /// "anything else" and maps to 502, per the brief for this provider.
        /// </summary>
        private async Task<JsonDocument> ReadOrThrowAsync(HttpResponseMessage response, string criteria, CancellationToken ct)
        {
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
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
