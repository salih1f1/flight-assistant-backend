using System.Text.Json;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.AviationStack
{
    /// <summary>
    /// Shared response handling for the AviationStack API.
    ///
    /// AviationStack signals failures with an <c>error</c> object in the JSON body — sometimes even
    /// under an HTTP 200 — so the body is inspected regardless of status code and <c>error.code</c>
    /// is mapped to an appropriate HTTP status. Both the flight status and flight search providers
    /// go through here so their error behaviour cannot drift apart.
    /// </summary>
    internal static class AviationStackResponse
    {
        // 400 Bad Request — the provider rejected a parameter we sent (should be prevented by our
        // own validators, so this is surfaced for diagnostics).
        private const int StatusCode400BadRequest = 400;

        // 500 Internal Server Error — an AviationStack account/plan/config problem on our side
        // (bad key, plan restriction).
        private const int StatusCode500InternalError = 500;

        // 502 Bad Gateway — the provider was unreachable or returned an unclassified error.
        private const int StatusCode502BadGateway = 502;

        // 503 Service Unavailable — the provider throttled us (rate/usage limit). The client may retry.
        private const int StatusCode503ServiceUnavailable = 503;

        /// <summary>
        /// Reads an AviationStack response, throwing <see cref="DomainException"/> for any
        /// provider-side failure. Returns the parsed document only on success; the caller owns
        /// disposing it.
        /// </summary>
        /// <param name="operation">Human-readable operation name used to prefix client-facing
        /// messages, e.g. <c>"Flight search"</c>.</param>
        /// <param name="context">Request context for logs only, e.g. <c>"SOF-&gt;IST 2026-10-10"</c>.
        /// Never include the request URL — it carries the API key.</param>
        public static async Task<JsonDocument> ReadOrThrowAsync(
            HttpResponseMessage response,
            string operation,
            string context,
            ILogger logger,
            CancellationToken ct)
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex,
                    "AviationStack returned an unparseable {Operation} response ({StatusCode}) for {Context}",
                    operation, status, context);
                throw new DomainException(
                    $"{operation} is temporarily unavailable: the flight data provider returned an unexpected response. Please try again shortly.",
                    StatusCode502BadGateway,
                    $"AviationStack returned HTTP {status} with a body that is not valid JSON.");
            }

            // AviationStack reports errors via an `error` object in the body, sometimes under HTTP 200.
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                var mapped = MapUpstreamError(error, status, operation, context, logger);
                doc.Dispose();
                throw mapped;
            }

            if (!response.IsSuccessStatusCode)
            {
                doc.Dispose();
                logger.LogWarning(
                    "AviationStack {Operation} returned {StatusCode} with no error body for {Context}",
                    operation, status, context);
                throw new DomainException(
                    $"{operation} is temporarily unavailable: the flight data provider responded with status {status}. Please try again shortly.",
                    StatusCode502BadGateway,
                    $"AviationStack returned HTTP {status} with no error object in the body.");
            }

            return doc;
        }

        /// <summary>
        /// Maps an AviationStack <c>error</c> object to a <see cref="DomainException"/> at an
        /// appropriate HTTP status. The provider code and message are logged and carried in
        /// <see cref="DomainException.DiagnosticDetail"/>; the client-facing message deliberately
        /// avoids leaking account/configuration details.
        /// </summary>
        private static DomainException MapUpstreamError(
            JsonElement error,
            int status,
            string operation,
            string context,
            ILogger logger)
        {
            var code = TryGetString(error, "code") ?? "unknown";
            var providerMessage = TryGetString(error, "message");

            // Safe to expose outside Production: the upstream status/code/message only, never the
            // request URL, which carries the access_key.
            var diagnostic = $"AviationStack returned HTTP {status} {code}: {providerMessage}";

            switch (code)
            {
                case "rate_limit_reached":
                case "usage_limit_reached":
                    logger.LogWarning(
                        "AviationStack throttled {Operation} ({Code}: {Message}) for {Context}",
                        operation, code, providerMessage, context);
                    return new DomainException(
                        $"{operation} is temporarily unavailable due to high demand. Please try again shortly.",
                        StatusCode503ServiceUnavailable,
                        diagnostic);

                case "invalid_access_key":
                case "missing_access_key":
                case "inactive_user":
                case "function_access_restricted":
                case "https_access_restricted":
                    // Our configuration problem — log loudly. Not "temporarily" unavailable: a bad
                    // key or a plan restriction will not clear up on its own.
                    logger.LogError(
                        "AviationStack rejected our credentials/plan on {Operation} ({Code}: {Message})",
                        operation, code, providerMessage);
                    return new DomainException(
                        $"{operation} is unavailable due to a flight data provider configuration issue.",
                        StatusCode500InternalError,
                        diagnostic);

                case "validation_error":
                    var detail = BuildValidationDetail(error, providerMessage);
                    logger.LogWarning(
                        "AviationStack rejected {Operation} parameters ({Code}: {Detail}) for {Context}",
                        operation, code, detail, context);
                    return new DomainException(
                        $"{operation} request was rejected by the flight data provider: {detail}",
                        StatusCode400BadRequest,
                        diagnostic);

                default:
                    logger.LogError(
                        "AviationStack {Operation} returned an error ({StatusCode} {Code}: {Message}) for {Context}",
                        operation, status, code, providerMessage, context);
                    return new DomainException(
                        $"{operation} is temporarily unavailable. Please try again shortly.",
                        StatusCode502BadGateway,
                        diagnostic);
            }
        }

        private static string BuildValidationDetail(JsonElement error, string? providerMessage)
        {
            // context: { "<field>": [ { "key": "...", "message": "..." }, ... ], ... }
            if (error.TryGetProperty("context", out var context) && context.ValueKind == JsonValueKind.Object)
            {
                var messages = context.EnumerateObject()
                    .SelectMany(field => field.Value.ValueKind == JsonValueKind.Array
                        ? field.Value.EnumerateArray()
                        : Enumerable.Empty<JsonElement>())
                    .Select(entry => TryGetString(entry, "message"))
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .ToList();

                if (messages.Count > 0)
                    return string.Join("; ", messages);
            }

            return string.IsNullOrWhiteSpace(providerMessage) ? "invalid request parameters" : providerMessage!;
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
