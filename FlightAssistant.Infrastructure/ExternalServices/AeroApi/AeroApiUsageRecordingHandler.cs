using System.Net;
using FlightAssistant.Domain.Configuration.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Meters every AeroAPI request, writing one <see cref="AeroApiCall"/> row per call that
    /// actually left the process. AeroAPI is billed per query on a $5/month tier, so this row is
    /// the ledger the usage rollup and the budget line are computed from.
    ///
    /// <para>
    /// <b>Placement is the whole design.</b> It is registered <i>third</i> on the
    /// <c>"AeroApi"</c> client — <c>Resilience → Throttle → Usage → transport</c> — which fixes two
    /// things that would otherwise both be wrong:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// A row exists <i>if and only if</i> a request reached the transport. Quota is consumed by
    /// sending, not by queueing, so a caller whose wait on
    /// <see cref="AeroApiThrottleGate"/> is cancelled never gets here and is never counted — the
    /// same reasoning behind the gate releasing abandoned reservations. Counting at queue entry
    /// would invent spend that never happened.
    /// </description></item>
    /// <item><description>
    /// Every retry attempt is its own row, because this sits <i>inside</i> the resilience handler
    /// and FlightAware bills each attempt separately. Counting once per logical request would
    /// under-report spend by up to <see cref="AeroApiOptions.MaxRetryAttempts"/>×.
    /// </description></item>
    /// </list>
    /// </summary>
    public sealed class AeroApiUsageRecordingHandler : DelegatingHandler
    {
        /// <summary>
        /// Ceiling on the metering write itself, and the reason it cannot simply run to completion:
        /// this handler is the innermost one, so the write is awaited <i>inside</i> Polly's
        /// <see cref="AeroApiOptions.AttemptTimeout"/> and <see cref="AeroApiOptions.TotalRequestTimeout"/>.
        /// A database that hangs rather than refuses — pool exhaustion, a failover — would burn the
        /// rest of the attempt window on a call whose response had already arrived, and Polly would
        /// then time the attempt out and <b>retry a request that had already succeeded</b>: up to
        /// <see cref="AeroApiOptions.MaxRetryAttempts"/>× the spend, caused by the very meter that
        /// exists to control spend, and a 502 for a flight lookup that was fine.
        ///
        /// <para>
        /// Five seconds is far longer than a single-row insert can legitimately need and far
        /// shorter than the slack in the attempt budget. Past it the row is abandoned and logged:
        /// one missing usage row is strictly cheaper than a re-billed AeroAPI query.
        /// </para>
        /// </summary>
        private static readonly TimeSpan MeteringWriteTimeout = TimeSpan.FromSeconds(5);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<AeroApiUsageRecordingHandler> _logger;

        public AeroApiUsageRecordingHandler(
            IServiceScopeFactory scopeFactory,
            TimeProvider timeProvider,
            ILogger<AeroApiUsageRecordingHandler> logger)
        {
            _scopeFactory = scopeFactory;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var endpoint = AeroApiEndpointTemplate.From(request.RequestUri);
            var method = request.Method.Method;
            var calledUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var startedAt = _timeProvider.GetTimestamp();

            int? statusCode = null;
            // Failed unless proven otherwise. Anything that reaches this handler has cleared the
            // throttle and been handed to the transport, so an exception on the way out — a
            // cancelled caller included — is treated as a call that was sent and is therefore
            // billed. Nothing here can tell a socket write that happened from one that was aborted
            // a moment before it, and for a budget meter over-counting that sliver is the safe
            // side: under-counting hides real spend.
            var outcome = AeroApiCallOutcome.Failed;

            try
            {
                var response = await base.SendAsync(request, cancellationToken);

                statusCode = (int)response.StatusCode;
                outcome = response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => AeroApiCallOutcome.Throttled,
                    _ when response.IsSuccessStatusCode => AeroApiCallOutcome.Success,
                    _ => AeroApiCallOutcome.Failed
                };

                return response;
            }
            finally
            {
                var durationMs = (long)_timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
                await RecordAsync(endpoint, method, statusCode, durationMs, outcome, calledUtc);
            }
        }

        /// <summary>
        /// Best-effort by contract: metering exists to observe flight requests, never to break one
        /// and never to slow one down materially. Any failure — a bad row, an unavailable database,
        /// a container without one at all, or a write that overruns
        /// <see cref="MeteringWriteTimeout"/> — is logged at Warning and swallowed.
        /// </summary>
        private async Task RecordAsync(
            AeroApiEndpointTemplate endpoint,
            string method,
            int? statusCode,
            long durationMs,
            AeroApiCallOutcome outcome,
            DateTime calledUtc)
        {
            try
            {
                var call = AeroApiCall.Record(
                    endpoint.Template, method, statusCode, durationMs, outcome, endpoint.Caller, calledUtc);

                // A message handler is owned by IHttpClientFactory and outlives every request
                // scope, so the scoped recorder is resolved per row rather than captured.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var recorder = scope.ServiceProvider.GetRequiredService<IAeroApiUsageRecorder>();

                // Deliberately NOT the caller's token: the call has already been sent and is
                // already billed, so a caller that walked away must not take the row with it —
                // that would under-report exactly the spend nobody is watching. It gets its own
                // budget instead, because unbounded is not an option here (see the constant).
                using var writeBudget = new CancellationTokenSource(MeteringWriteTimeout, _timeProvider);
                await recorder.RecordAsync(call, writeBudget.Token);
            }
            catch (Exception ex)
            {
                // Template and outcome only — never the request URI, which carries the query
                // string, and never anything from the request headers, which carry the API key.
                _logger.LogWarning(ex,
                    "Failed to record AeroAPI usage for {EndpointTemplate} ({Outcome}). The request itself was unaffected.",
                    endpoint.Template, outcome);
            }
        }
    }
}
