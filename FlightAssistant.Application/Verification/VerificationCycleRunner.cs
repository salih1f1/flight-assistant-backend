using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.Verification
{
    /// <summary>One cycle of the reference-data verification job (#367, spec §4.2).</summary>
    public interface IVerificationCycleRunner
    {
        /// <summary>Resets stuck requests, claims a batch, processes each claimed request and
        /// releases source caches no open request uses.</summary>
        /// <exception cref="VerificationCycleFailedException">The batch was processed, then releasing
        /// source caches failed; carries the batch's counts.</exception>
        Task<VerificationCycleResult> RunCycleAsync(CancellationToken ct = default);
    }

    /// <param name="StuckReset">Stuck Running requests the reset returned to Pending, one attempt added.</param>
    /// <param name="StuckAbandoned">Stuck Running requests the reset completed as Error because they ran
    /// out of attempts. Not part of <see cref="RequestsProcessed"/>: this cycle did not claim them.</param>
    /// <param name="RequestsProcessed">Requests claimed and worked through this cycle.</param>
    /// <param name="RequestsFailed">The subset that ended in a source error (retry scheduled or
    /// <c>Error</c>), an unusable source, a deleted entity or an unexpected exception, whether or not
    /// that exception could be recorded. A mismatch or not-found is a completed check, not a failure;
    /// a lost claim is neither.</param>
    /// <param name="ClaimsLost">The subset another claim took over; nothing was written for them.</param>
    /// <param name="FailuresUnrecorded">The subset of <see cref="RequestsFailed"/> whose failure could not
    /// be recorded either: still Running, with no attempt counted until the stuck-request reset.</param>
    public sealed record VerificationCycleResult(
        int StuckReset, int StuckAbandoned, int RequestsProcessed, int RequestsFailed, int ClaimsLost, int FailuresUnrecorded);

    /// <summary>
    /// Runs one verification cycle. Resolved from the job's cycle scope (which carries the system
    /// actor, set by <c>ReferenceVerificationBackgroundService</c>), where it only resets stuck
    /// requests and reads which sources open requests use: nothing is tracked or saved on that
    /// context.
    ///
    /// <para>
    /// <b>Scopes (controller ruling).</b> The batch is claimed in a scope of its own, and each claimed
    /// request is processed and saved in a new scope, created with <see cref="VerificationJobActor"/>
    /// so every save is audited under the system actor. A save that fails leaves its changes tracked
    /// on that scope's context; a new scope per request means the next request never saves them, and
    /// an exception is recorded as a source error from yet another fresh scope.
    /// </para>
    ///
    /// <para>
    /// <b>Isolation.</b> An exception while processing one request never stops the batch: it is logged
    /// and recorded as a source error for that request. Only cancellation of the cycle's token
    /// propagates, leaving the unprocessed requests Running for the stuck-request reset.
    /// </para>
    /// </summary>
    public sealed class VerificationCycleRunner : IVerificationCycleRunner
    {
        /// <summary>A Running request whose claim started longer ago than this is reset as stuck: the
        /// reset counts an attempt, and returns it to Pending, or completes it as Error when that was
        /// its last attempt.</summary>
        public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

        private readonly IVerificationRequestRepository _requests;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IVerificationSourceRegistry _registry;
        private readonly IVerificationJobOptions _options;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<VerificationCycleRunner> _logger;

        public VerificationCycleRunner(
            IVerificationRequestRepository requests,
            IServiceScopeFactory scopeFactory,
            IVerificationSourceRegistry registry,
            IVerificationJobOptions options,
            TimeProvider timeProvider,
            ILogger<VerificationCycleRunner> logger)
        {
            _requests = requests;
            _scopeFactory = scopeFactory;
            _registry = registry;
            _options = options;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task<VerificationCycleResult> RunCycleAsync(CancellationToken ct = default)
        {
            // One id per cycle, on every scope's AdminContext (so on the audit rows' TraceId) and
            // on every log line of the cycle.
            var cycleId = Guid.NewGuid().ToString("N");
            using var logScope = _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = cycleId });

            var now = _timeProvider.GetUtcNow().UtcDateTime;

            var reset = await _requests.ResetStuckRunningAsync(now - StuckAfter, _options.MaxAttempts, now, ct);
            if (reset.ReturnedToPending > 0)
                _logger.LogWarning(
                    "Returned {Count} verification request(s) stuck in Running for over {StuckAfter} to Pending, counting an attempt each.",
                    reset.ReturnedToPending, StuckAfter);
            if (reset.Abandoned > 0)
                _logger.LogError(
                    "Completed {Count} verification request(s) as Error: stuck in Running for over {StuckAfter} with no attempts left.",
                    reset.Abandoned, StuckAfter);

            var claims = await ClaimAsync(now, cycleId, ct);

            var failed = 0;
            var lost = 0;
            var unrecorded = 0;
            foreach (var claim in claims)
            {
                ct.ThrowIfCancellationRequested();

                switch (await ProcessOneAsync(claim, cycleId, ct))
                {
                    case VerificationProcessOutcome.RetryScheduled:
                    case VerificationProcessOutcome.Error:
                        failed++;
                        break;
                    case VerificationProcessOutcome.ClaimLost:
                        lost++;
                        break;
                    case VerificationProcessOutcome.FailureUnrecorded:
                        failed++;
                        unrecorded++;
                        break;
                }
            }

            var result = new VerificationCycleResult(reset.ReturnedToPending, reset.Abandoned, claims.Count, failed, lost, unrecorded);
            if (claims.Count > 0)
                _logger.LogInformation(
                    "Verification cycle processed {Processed} request(s): {Failed} failed, {Lost} claim(s) lost.",
                    claims.Count, failed, lost);

            try
            {
                await ReleaseUnusedSourcesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The batch is done and its outcomes are saved; the cycle still failed, but with real counts.
                throw new VerificationCycleFailedException(result, ex);
            }

            return result;
        }

        private async Task<IReadOnlyList<VerificationClaim>> ClaimAsync(DateTime now, string cycleId, CancellationToken ct)
        {
            // The claimed requests come back tracked; this context is disposed without saving, and
            // each request is re-loaded in its own scope.
            using var scope = VerificationJobActor.CreateScope(_scopeFactory, cycleId);
            var claimed = await scope.ServiceProvider
                .GetRequiredService<IVerificationRequestRepository>()
                .ClaimBatchAsync(_options.BatchSize, now, ct);

            return claimed.Select(VerificationClaim.From).ToList();
        }

        private async Task<VerificationProcessOutcome> ProcessOneAsync(VerificationClaim claim, string cycleId, CancellationToken ct)
        {
            Exception failure;
            try
            {
                using var scope = VerificationJobActor.CreateScope(_scopeFactory, cycleId);
                return await scope.ServiceProvider
                    .GetRequiredService<VerificationRequestProcessor>()
                    .ProcessAsync(claim, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Verification request {RequestId} failed unexpectedly; recording it as a source error.", claim.RequestId);
                failure = ex;
            }

            try
            {
                using var scope = VerificationJobActor.CreateScope(_scopeFactory, cycleId);
                return await scope.ServiceProvider
                    .GetRequiredService<VerificationRequestProcessor>()
                    .RecordUnexpectedFailureAsync(claim, failure, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Could not record the failure of verification request {RequestId}; it stays Running until the stuck-request reset counts the attempt.",
                    claim.RequestId);
                return VerificationProcessOutcome.FailureUnrecorded;
            }
        }

        /// <summary>
        /// Drops the cached dataset of every source no open request uses (spec §4.3). The registry
        /// has no EF dependency (controller ruling); this is where the queue is asked.
        /// </summary>
        private async Task ReleaseUnusedSourcesAsync(CancellationToken ct)
        {
            var inUse = new List<string>();
            foreach (var source in _registry.Sources)
            {
                if (await _requests.AnyOpenRequestUsesSourceAsync(source.Name, ct))
                    inUse.Add(source.Name);
            }

            _registry.ReleaseUnused(inUse);
        }
    }
}
