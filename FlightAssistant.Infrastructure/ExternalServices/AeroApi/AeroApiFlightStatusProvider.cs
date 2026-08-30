using System.Globalization;
using System.Text.Json;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Fetches live flight status from FlightAware AeroAPI's <c>GET /flights/{ident}</c>, which
    /// returns a short history of recent (and, for future-dated service, upcoming) operations of
    /// the given ident rather than a single current snapshot — so the row matching the requested
    /// <see cref="DateOnly"/> service date must be picked out client-side. That comparison is made
    /// against the row's <b>origin-local</b> calendar date, using the <c>origin.timezone</c> each
    /// row already carries — see <see cref="ServiceDateOf"/> for why a UTC comparison silently
    /// selects the wrong day's operation.
    ///
    /// <para>
    /// <b>Status is prose, not a code.</b> AeroAPI's <c>status</c> field is a human-readable
    /// string — observed values include <c>"Scheduled"</c> and <c>"Arrived / Gate Arrival"</c>;
    /// FlightAware's own documentation and other integrations additionally show <c>"En Route"</c>,
    /// <c>"Delayed"</c>, <c>"Diverted"</c> and <c>"Cancelled"</c> qualified with a "/ …" suffix
    /// (e.g. <c>"En Route / On Time"</c>). <see cref="MapStatusCode"/> matches only the leading
    /// segment before any "/" against the small set of prefixes this provider has actually seen or
    /// can find documented, case-insensitively. Anything else is logged at Warning — not silently
    /// coerced — and reported as <c>"UNK"</c>, the "not actionable" fallback for any status this
    /// provider does not recognise. <c>cancelled: true</c> always wins over
    /// <c>status</c> text, since AeroAPI can report a stale "Scheduled"/"En Route" string on a
    /// flight that has in fact been cancelled.
    /// </para>
    /// </summary>
    public sealed class AeroApiFlightStatusProvider : IFlightStatusProvider
    {
        private const string Operation = "Flight status";

        // 502 Bad Gateway — the provider was unreachable (no HTTP response at all).
        private const int StatusCode502BadGateway = 502;

        private const string StatusScheduled = "SCH";
        private const string StatusDeparted = "DEP";
        private const string StatusArrived = "ARR";
        private const string StatusDelayed = "DLY";
        private const string StatusCancelled = "CAN";
        private const string StatusDiverted = "DIV";
        private const string StatusUnknown = "UNK";

        private readonly HttpClient _http;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<AeroApiFlightStatusProvider> _logger;
        private readonly string _apiKey;

        public string ProviderName => "AeroAPI";

        public AeroApiFlightStatusProvider(
            HttpClient http,
            IOptions<AeroApiOptions> options,
            TimeProvider timeProvider,
            ILogger<AeroApiFlightStatusProvider> logger)
        {
            _http = http;
            _timeProvider = timeProvider;
            _logger = logger;
            _apiKey = options.Value.ApiKey;
        }

        public async Task<FlightStatusResult?> GetStatusAsync(
            string flightNumber, DateOnly serviceDate, CancellationToken ct = default)
        {
            var ident = flightNumber.ToUpperInvariant();
            var criteria = $"{ident} {serviceDate:yyyy-MM-dd}";

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync($"flights/{Uri.EscapeDataString(ident)}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AeroAPI flight status request failed for {Criteria}", criteria);
                throw new DomainException(
                    $"{Operation} is temporarily unavailable. Please try again shortly.",
                    ex,
                    StatusCode502BadGateway);
            }

            using var doc = await AeroApiResponse.ReadOrThrowAsync(
                response, Operation, criteria, _apiKey, _logger, ct);

            if (!doc.RootElement.TryGetProperty("flights", out var flights)
                || flights.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var flight in flights.EnumerateArray())
            {
                var scheduledOut = TryGetUtcDateTime(flight, "scheduled_out");
                if (scheduledOut is null) continue;
                if (ServiceDateOf(flight, scheduledOut.Value, criteria) != serviceDate) continue;

                return MapToResult(flight, criteria);
            }

            // No row for the requested service date — "no data", not a provider failure.
            return null;
        }

        /// <summary>
        /// The service date a row belongs to, expressed the same way the rest of the system
        /// expresses it: the calendar date at the <b>origin airport's local time</b>.
        ///
        /// <para>
        /// This is not cosmetic. <c>AeroApiFlightSearchProvider</c> returns
        /// <c>FlightSearchResult.ServiceDate</c> as the origin's local service date (the whole
        /// point of its local-day window shift), <c>TrackFlightCommandHandler</c> stores that
        /// verbatim, and <c>SyncFlightStatusCommand</c> hands it straight back here. Matching on
        /// the UTC calendar date instead would, for any airport far enough from UTC, skip the real
        /// operation and select the <i>next</i> day's — applying the wrong day's gate, terminal and
        /// status to the tracked flight. AKL (UTC+13) departing 07:00 local is 18:00Z the previous
        /// day, so a UTC comparison misses it by exactly one operation.
        /// </para>
        ///
        /// <para>
        /// Costs no extra call: <c>/flights/{ident}</c> rows already carry
        /// <c>origin.timezone</c> as an IANA id. When it is absent or unloadable the UTC calendar
        /// date is used as before — an approximation that is right for most of the world, and far
        /// better than refusing to match anything.
        /// </para>
        /// </summary>
        private DateOnly ServiceDateOf(JsonElement flight, DateTime scheduledOutUtc, string criteria)
        {
            var timezoneId = flight.TryGetProperty("origin", out var origin)
                             && origin.ValueKind == JsonValueKind.Object
                ? TryGetString(origin, "timezone")
                : null;

            if (string.IsNullOrWhiteSpace(timezoneId))
            {
                _logger.LogDebug(
                    "AeroAPI row for {Criteria} carries no origin timezone; matching on the UTC calendar date instead",
                    criteria);
                return DateOnly.FromDateTime(scheduledOutUtc);
            }

            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
                var local = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(scheduledOutUtc, DateTimeKind.Utc), zone);
                return DateOnly.FromDateTime(local);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                _logger.LogWarning(ex,
                    "Timezone id {TimezoneId} on an AeroAPI row for {Criteria} could not be loaded; matching on the UTC calendar date instead",
                    timezoneId, criteria);
                return DateOnly.FromDateTime(scheduledOutUtc);
            }
        }

        private FlightStatusResult MapToResult(JsonElement flight, string criteria)
        {
            var cancelled = flight.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.True;
            var diverted = flight.TryGetProperty("diverted", out var d) && d.ValueKind == JsonValueKind.True;
            var statusRaw = TryGetString(flight, "status");
            var statusCode = MapStatusCode(statusRaw, cancelled, diverted, criteria);

            var estimatedOut = TryGetUtcDateTime(flight, "estimated_out");
            var estimatedIn = TryGetUtcDateTime(flight, "estimated_in");
            var actualOut = TryGetUtcDateTime(flight, "actual_out");
            var actualIn = TryGetUtcDateTime(flight, "actual_in");
            var scheduledOut = TryGetUtcDateTime(flight, "scheduled_out");

            var effectiveAt = actualOut ?? estimatedOut ?? scheduledOut ?? _timeProvider.GetUtcNow().UtcDateTime;

            return new FlightStatusResult(
                StatusCode: statusCode,
                EffectiveAtUtc: effectiveAt,
                EstimatedDepartureUtc: estimatedOut,
                EstimatedArrivalUtc: estimatedIn,
                ActualDepartureUtc: actualOut,
                ActualArrivalUtc: actualIn,
                Gate: TryGetString(flight, "gate_origin"),
                Terminal: TryGetString(flight, "terminal_origin"),
                BaggageClaim: TryGetString(flight, "baggage_claim"),
                EquipmentIcao: TryGetString(flight, "aircraft_type"),
                ExternalId: TryGetString(flight, "fa_flight_id"),
                RawPayloadJson: flight.GetRawText()
            );
        }

        /// <summary>
        /// Normalises AeroAPI's prose <c>status</c> to the <c>FlightStatus.Code</c> vocabulary
        /// (<c>SCH</c>/<c>DEP</c>/<c>ARR</c>/<c>DLY</c>/<c>CAN</c>/<c>DIV</c>/<c>UNK</c>).
        /// <paramref name="cancelled"/> and <paramref name="diverted"/> are checked first and
        /// unconditionally win, in that order — AeroAPI supplies both as booleans and can report a
        /// stale "Scheduled"/"En Route" string on a flight that has in fact been cancelled or
        /// diverted, so the prose must never be allowed to mask either. (A flight cannot be both;
        /// if the payload claims it is, cancellation is the more consequential of the two for a
        /// traveller and wins.) Only the segment before the first "/" is
        /// matched (AeroAPI qualifies some statuses, e.g. <c>"Arrived / Gate Arrival"</c>), compared
        /// case-insensitively against a deliberately small, documented set — anything outside it is
        /// logged, not guessed at.
        /// </summary>
        private string MapStatusCode(string? status, bool cancelled, bool diverted, string criteria)
        {
            if (cancelled) return StatusCancelled;
            if (diverted) return StatusDiverted;

            var prefix = status?.Split('/')[0].Trim().ToLowerInvariant();

            return prefix switch
            {
                "scheduled" => StatusScheduled,
                "en route" => StatusDeparted,
                "departed" => StatusDeparted,
                "arrived" => StatusArrived,
                "landed" => StatusArrived,
                "delayed" => StatusDelayed,
                "diverted" => StatusDiverted,
                "cancelled" => StatusCancelled,
                _ => LogUnmappedStatus(status, criteria)
            };
        }

        private string LogUnmappedStatus(string? status, string criteria)
        {
            _logger.LogWarning(
                "AeroAPI returned an unrecognised flight status {Status} for {Criteria}; mapping to {Fallback}",
                status, criteria, StatusUnknown);
            return StatusUnknown;
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
