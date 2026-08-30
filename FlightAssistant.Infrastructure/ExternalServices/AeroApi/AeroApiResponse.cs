using System.Net;
using System.Text.Json;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Shared response handling for the FlightAware AeroAPI.
    ///
    /// Unlike AviationStack (see <c>AviationStackResponse</c>), AeroAPI signals failures with real
    /// HTTP status codes rather than an <c>error</c> object hidden inside an HTTP 200 body, so there
    /// is no need to inspect a successful body for a failure marker. Both the future flight status
    /// and flight search providers go through here so their error behaviour cannot drift apart.
    /// </summary>
    internal static class AeroApiResponse
    {
        // 400 Bad Request — the provider rejected a parameter we sent.
        private const int StatusCode400BadRequest = 400;

        // 500 Internal Server Error — our credentials/plan are the problem (401/403 from AeroAPI),
        // not something the caller can retry their way out of.
        private const int StatusCode500InternalError = 500;

        // 502 Bad Gateway — the provider was unreachable or returned an unclassified error.
        private const int StatusCode502BadGateway = 502;

        // 503 Service Unavailable — the provider throttled us (429). The client may retry later.
        private const int StatusCode503ServiceUnavailable = 503;

        /// <summary>
        /// Reads an AeroAPI response, throwing <see cref="DomainException"/> for any provider-side
        /// failure. Returns the parsed document only on success; the caller owns disposing it.
        /// </summary>
        /// <param name="operation">Human-readable operation name used in logs, e.g.
        /// <c>"Flight search"</c>.</param>
        /// <param name="criteria">Request context for logs only, e.g. <c>"SOF-&gt;IST 2026-10-10"</c>.
        /// Never include the request URL or headers — the URL is safe (the key travels only in the
        /// <c>x-apikey</c> header) but headers must never be logged.</param>
        public static async Task<JsonDocument> ReadOrThrowAsync(
            HttpResponseMessage response,
            string operation,
            string criteria,
            ILogger logger,
            CancellationToken ct)
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
                    logger.LogError(ex,
                        "AeroAPI returned an unparseable {Operation} response ({StatusCode}) for {Criteria}",
                        operation, status, criteria);
                    throw new DomainException(
                        "Flight search is temporarily unavailable. Please try again shortly.",
                        StatusCode502BadGateway,
                        $"AeroAPI returned HTTP {status} with a body that is not valid JSON.");
                }
            }

            var upstreamBody = await SafeReadBodyAsync(response, ct);

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    // Our configuration problem — log loudly. Not "temporarily" unavailable: a bad
                    // key will not clear up on its own.
                    logger.LogError(
                        "AeroAPI rejected our credentials on {Operation} ({StatusCode}) for {Criteria}: {Body}",
                        operation, status, criteria, upstreamBody);
                    throw new DomainException(
                        "Flight data is temporarily unavailable.",
                        StatusCode500InternalError,
                        $"AeroAPI returned HTTP {status}: {upstreamBody}");

                case HttpStatusCode.TooManyRequests:
                    var retryAfter = DescribeRetryAfter(response);
                    logger.LogWarning(
                        "AeroAPI throttled {Operation} ({StatusCode}) for {Criteria}{RetryAfterSuffix}",
                        operation, status, criteria, retryAfter is null ? "" : $" (Retry-After: {retryAfter})");
                    throw new DomainException(
                        retryAfter is null
                            ? "Flight search is busy. Please try again shortly."
                            : $"Flight search is busy. Please try again shortly. Retry-After: {retryAfter}.",
                        StatusCode503ServiceUnavailable,
                        $"AeroAPI returned HTTP 429: {upstreamBody}");

                case HttpStatusCode.BadRequest:
                    logger.LogWarning(
                        "AeroAPI rejected {Operation} parameters ({StatusCode}) for {Criteria}: {Body}",
                        operation, status, criteria, upstreamBody);
                    throw new DomainException(
                        "The flight search request was rejected.",
                        StatusCode400BadRequest,
                        $"AeroAPI returned HTTP {status}: {upstreamBody}");

                default:
                    logger.LogError(
                        "AeroAPI {Operation} returned an unexpected status ({StatusCode}) for {Criteria}: {Body}",
                        operation, status, criteria, upstreamBody);
                    throw new DomainException(
                        "Flight search is temporarily unavailable. Please try again shortly.",
                        StatusCode502BadGateway,
                        $"AeroAPI returned HTTP {status}: {upstreamBody}");
            }
        }

        /// <summary>Formats the upstream <c>Retry-After</c> header (seconds or HTTP-date form) for
        /// the client-facing message and logs. Never throws on a malformed/absent header.</summary>
        private static string? DescribeRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter is null)
                return null;

            if (retryAfter.Delta is { } delta)
                return $"{Math.Max(0, (int)delta.TotalSeconds)}s";

            if (retryAfter.Date is { } date)
                return date.ToString("O");

            return null;
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
