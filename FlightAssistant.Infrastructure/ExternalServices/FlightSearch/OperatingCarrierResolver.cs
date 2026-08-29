using System.Globalization;
using System.Text.Json;
using FlightAssistant.Infrastructure.ExternalServices.AviationStack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.FlightSearch
{
    /// <summary>
    /// Works out which carrier operates a future-dated departure, which the schedules endpoint
    /// does not disclose.
    ///
    /// Airline schedules repeat weekly, and the real-time endpoint marks codeshares exactly via
    /// <c>flight.codeshared</c>. So the same route on a recent matching weekday answers the
    /// question: build a map of local departure time → operating flight number from that day, then
    /// apply it to the future date.
    ///
    /// Deliberately best-effort. A route that did not run on the reference day — new, seasonal, or
    /// less than weekly — simply yields nothing and the caller leaves the operating carrier null,
    /// which is the honest answer rather than a guess.
    ///
    /// <c>/v1/routes</c> is NOT usable for this. It looks like an operating/marketing
    /// discriminator on small samples but is not one: 6E4122 is a confirmed codeshare of TK1030
    /// and still returns a route, so its coverage is merely patchy.
    /// </summary>
    public sealed class OperatingCarrierResolver
    {
        private const string Operation = "Operating carrier lookup";

        /// <summary>
        /// How far back to look for a matching weekday. Seven days is the nearest same-weekday
        /// date, and well inside the real-time endpoint's history.
        /// </summary>
        private const int ReferenceDaysBack = 7;

        private readonly HttpClient _http;
        private readonly ILogger<OperatingCarrierResolver> _logger;
        private readonly string _apiKey;

        public OperatingCarrierResolver(
            HttpClient http,
            IConfiguration config,
            ILogger<OperatingCarrierResolver> logger)
        {
            _http = http;
            _logger = logger;
            _apiKey = config["AviationStack:ApiKey"] ?? string.Empty;
        }

        /// <summary>
        /// Maps "HH:mm local departure" → operating flight number for the given route, learned from
        /// the most recent matching weekday. Empty when nothing could be learned.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, string>> LearnAsync(
            string departureIata,
            string? arrivalIata,
            DateOnly futureDate,
            CancellationToken ct = default)
        {
            var reference = futureDate.AddDays(-WeeksBackTo(futureDate) * 7);

            try
            {
                var query = new List<string>
                {
                    $"access_key={Uri.EscapeDataString(_apiKey)}",
                    $"dep_iata={Uri.EscapeDataString(departureIata)}",
                    $"dep_scheduled_time_dep={reference:yyyy-MM-dd}"
                };
                if (!string.IsNullOrWhiteSpace(arrivalIata))
                    query.Add($"arr_iata={Uri.EscapeDataString(arrivalIata)}");

                var response = await _http.GetAsync($"flights?{string.Join("&", query)}", ct);

                using var doc = await AviationStackResponse.ReadOrThrowAsync(
                    response, Operation, $"{departureIata}->{arrivalIata ?? "*"} {reference:yyyy-MM-dd}", _logger, ct);

                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    return ReadOnlyDictionary.Empty;

                var map = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var element in data.EnumerateArray())
                {
                    var flight = element.TryGetProperty("flight", out var f) ? f : default;
                    var dep = element.TryGetProperty("departure", out var d) ? d : default;

                    // Only the operating row (no `codeshared` object) names the operator.
                    if (flight.ValueKind != JsonValueKind.Object) continue;
                    if (flight.TryGetProperty("codeshared", out var cs) && cs.ValueKind == JsonValueKind.Object)
                        continue;

                    var number = TryGetString(flight, "iata");
                    var slot = TryGetLocalTimeOfDay(dep, "scheduled");
                    if (number is null || slot is null) continue;

                    map[slot] = number.ToUpperInvariant();
                }

                if (map.Count == 0)
                    _logger.LogInformation(
                        "No operating-carrier reference found for {Route} on {ReferenceDate}; "
                        + "future results keep a null operating flight number",
                        $"{departureIata}->{arrivalIata ?? "*"}", reference);

                return map;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort enrichment: never fail a search over it.
                _logger.LogWarning(ex,
                    "Could not learn operating carriers for {Route}; results keep a null operating flight number",
                    $"{departureIata}->{arrivalIata ?? "*"}");
                return ReadOnlyDictionary.Empty;
            }
        }

        /// <summary>
        /// Whole weeks back from <paramref name="futureDate"/> to the most recent past date on the
        /// same weekday, so the reference day genuinely shares the timetable.
        /// </summary>
        private static int WeeksBackTo(DateOnly futureDate)
        {
            var daysAhead = futureDate.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber;
            return Math.Max(1, (int)Math.Ceiling((daysAhead + ReferenceDaysBack) / 7.0));
        }

        /// <summary>
        /// The wall-clock "HH:mm" of a departure. Matching on local time rather than UTC keeps the
        /// map valid across a DST change between the reference day and the searched date.
        /// </summary>
        private static string? TryGetLocalTimeOfDay(JsonElement el, string field)
        {
            if (el.ValueKind == JsonValueKind.Undefined) return null;
            if (el.TryGetProperty(field, out var val) && val.ValueKind == JsonValueKind.String)
                return DateTimeOffset.TryParse(
                    val.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
                        ? dto.DateTime.ToString("HH:mm", CultureInfo.InvariantCulture)
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

        private static class ReadOnlyDictionary
        {
            public static readonly IReadOnlyDictionary<string, string> Empty =
                new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
