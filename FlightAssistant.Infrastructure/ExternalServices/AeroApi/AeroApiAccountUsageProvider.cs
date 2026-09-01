using System.Globalization;
using System.Text.Json;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Reads account usage and spend from FlightAware AeroAPI.
    ///
    /// Endpoint: GET /account/usage?start=&amp;end=
    ///
    /// <para>
    /// <b>Why this exists instead of a local meter.</b> AeroAPI bills per <i>page</i> (a four-page
    /// search costs four units, not one) and bills failed calls as well as successful ones. Any
    /// locally counted, locally priced model gets both of those wrong and can never be reconciled
    /// against the invoice, so the provider's own report is the only authority. Conveniently, this
    /// endpoint is itself billed at $0.00 — polling it consumes budget only in the sense that it
    /// takes a turn on the shared outbound throttle.
    /// </para>
    ///
    /// <para>
    /// Runs on the shared <c>"AeroApi"</c> named client, so it inherits the <c>x-apikey</c> header,
    /// the resilience pipeline and <see cref="AeroApiThrottleGate"/> exactly like the flight search
    /// and status providers, and maps failures through the same
    /// <see cref="AeroApiResponse.ReadOrThrowAsync"/> so their status mapping cannot drift apart.
    /// </para>
    /// </summary>
    public sealed class AeroApiAccountUsageProvider : IAeroApiAccountUsageProvider
    {
        private const string Operation = "Usage reporting";

        // 502 Bad Gateway — the provider was unreachable (no HTTP response at all).
        private const int StatusCode502BadGateway = 502;

        // AeroAPI accepts a date or an ISO-8601 instant; a bare date would silently mean 00:00:00Z,
        // so the full instant is always sent.
        private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

        // Every top-level total this provider reads — ReadInt/ReadDecimal below return 0/0m for any
        // of these that is missing or not a JSON number, which is a correct default for the field
        // AeroAPI stops sending but a dangerously quiet one for a field it sends malformed: a
        // metered account would report $0.00 spend with nothing in the log to say why.
        private static readonly string[] TotalFields =
        [
            "total_calls", "total_pages", "total_cost", "total_discount_cost",
            "total_successful_calls", "total_failed_calls"
        ];

        private readonly HttpClient _http;
        private readonly ILogger<AeroApiAccountUsageProvider> _logger;
        private readonly string _apiKey;

        public AeroApiAccountUsageProvider(
            HttpClient http,
            IOptions<AeroApiOptions> options,
            ILogger<AeroApiAccountUsageProvider> logger)
        {
            _http = http;
            _logger = logger;
            _apiKey = options.Value.ApiKey;
        }

        public async Task<AeroApiAccountUsage> GetUsageAsync(
            DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct = default)
        {
            var start = Format(startUtc);
            var end = Format(endUtc);
            var criteria = $"{start}..{end}";

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(
                    $"account/usage?start={Uri.EscapeDataString(start)}&end={Uri.EscapeDataString(end)}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AeroAPI usage request failed for {Criteria}", criteria);
                throw new DomainException(
                    $"{Operation} is temporarily unavailable. Please try again shortly.",
                    ex,
                    StatusCode502BadGateway);
            }

            using var doc = await AeroApiResponse.ReadOrThrowAsync(
                response, Operation, criteria, _apiKey, _logger, ct);

            var root = doc.RootElement;

            // A usage report is a dashboard number, not a decision input, so an individual field
            // AeroAPI stops sending reads as zero rather than failing the whole request. A total
            // that is missing, or present but not a JSON number (e.g. "total_cost": "0.66"), is a
            // different matter — that is a response shape change, and silently reporting $0.00 spend
            // on a metered account is exactly the kind of quiet wrong answer this feature exists to
            // remove, so every such field is named in the log.
            var unreadableTotals = root.ValueKind == JsonValueKind.Object
                ? TotalFields.Where(name => !IsReadableNumber(root, name)).ToList()
                : TotalFields.ToList();

            if (unreadableTotals.Count > 0)
            {
                _logger.LogWarning(
                    "AeroAPI usage response for {Criteria} had missing or unreadable total field(s) " +
                    "{Fields} — reporting zero for those",
                    criteria, string.Join(", ", unreadableTotals));
            }

            return new AeroApiAccountUsage(
                ReadInt(root, "total_calls"),
                ReadInt(root, "total_pages"),
                ReadDecimal(root, "total_cost"),
                ReadDecimal(root, "total_discount_cost"),
                ReadInt(root, "total_successful_calls"),
                ReadInt(root, "total_failed_calls"),
                ReadOperations(root));
        }

        private static IReadOnlyList<AeroApiOperationUsage> ReadOperations(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("resource_details", out var details)
                || details.ValueKind != JsonValueKind.Array)
                return [];

            var rows = new List<AeroApiOperationUsage>();
            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind != JsonValueKind.Object) continue;

                // AeroAPI normalises `operation` itself (e.g. "GET /schedules/{date_start}/{date_end}"),
                // so there is no client-side path templating anywhere in this feature.
                var operation = detail.TryGetProperty("operation", out var op)
                                && op.ValueKind == JsonValueKind.String
                    ? op.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(operation)) continue;

                rows.Add(new AeroApiOperationUsage(
                    operation,
                    ReadInt(detail, "total_resource_calls"),
                    ReadInt(detail, "num_pages"),
                    ReadDecimal(detail, "resource_cost"),
                    ReadInt(detail, "successful_resource_calls"),
                    ReadInt(detail, "failed_resource_calls")));
            }

            return rows;
        }

        private static bool IsReadableNumber(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number;

        private static int ReadInt(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? parsed
                : 0;

        private static decimal ReadDecimal(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var parsed)
                ? parsed
                : 0m;

        private static string Format(DateTimeOffset instant) =>
            instant.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
    }
}
