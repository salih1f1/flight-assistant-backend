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
        /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled. Claims the
        /// cycle had not finished are first returned to Pending, best effort.</exception>
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
    /// requests, reads which sources open requests use and returns unfinished claims on shutdown:
    /// nothing is tracked or saved on that context.
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
    /// propagates. Before it does, the claims the cycle had not finished (the one in progress and every
    /// later one) are returned to Pending without counting an attempt, so a deploy or restart doesn't
    /// spend an attempt on each of them. That step is best effort, on a short timeout of its own
    /// (<see cref="ReturnClaimsTimeout"/>): if it fails, those requests stay Running until the
    /// stuck-request reset, which counts the attempt.
    /// </para>
    ///
    /// <para>
    /// <b>Releasing datasets.</b> After the batch, the cycle drops the cached dataset of every source no
    /// open request uses. When no source holds anything, that step is skipped, so an idle queue costs
    /// no query for it.
    /// </para>
    /// </summary>
    public sealed class VerificationCycleRunner : IVerificationCycleRunner
    {
        /// <summary>A Running request whose claim started longer ago than this is reset as stuck: the
        /// reset counts an attempt, and returns it to Pending, or completes it as Error when that was
        /// its last attempt.</summary>
        public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

        /// <summary>How long returning unfinished claims to Pending may take once the cycle is
        /// cancelled. Short, because the host is shutting down; the stuck-request reset is the fallback.</summary>
        public static readonly TimeSpan ReturnClaimsTimeout = TimeSpan.FromSeconds(5);

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
            var next = 0;
            try
            {
                for (; next < claims.Count; next++)
                {
                    ct.ThrowIfCancellationRequested();

                    switch (await ProcessOneAsync(claims[next], cycleId, ct))
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
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await ReturnUnfinishedClaimsAsync(claims.Skip(next).ToList());
                throw;
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
        /// On a cancelled cycle, returns the claims it had not finished to Pending with no attempt
        /// counted, in one repository call guarded by each claim's own <c>StartedUtc</c>, so a claim
        /// another worker has taken over (or an outcome that was saved after all) is not touched. Runs
        /// on its own <see cref="ReturnClaimsTimeout"/>, never on the cancelled token. A failure is
        /// logged and swallowed: the stuck-request reset still reclaims those requests.
        /// </summary>
        private async Task ReturnUnfinishedClaimsAsync(IReadOnlyCollection<VerificationClaim> unfinished)
        {
            if (unfinished.Count == 0) return;

            try
            {
                using var timeout = new CancellationTokenSource(ReturnClaimsTimeout, _timeProvider);
                var returned = await _requests.ReturnClaimsToPendingAsync(unfinished, timeout.Token);
                _logger.LogInformation(
                    "Verification cycle cancelled: returned {Returned} of {Unfinished} unfinished claim(s) to Pending without counting an attempt.",
                    returned, unfinished.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Verification cycle cancelled: could not return {Unfinished} unfinished claim(s) to Pending; the stuck-request reset reclaims them after {StuckAfter}, counting an attempt.",
                    unfinished.Count, StuckAfter);
            }
        }

        /// <summary>
        /// Drops the cached dataset of every source no open request uses (spec §4.3). The registry
        /// has no EF dependency (controller ruling); this is where the queue is asked, in one query,
        /// and only when some source holds something to drop.
        /// </summary>
        private async Task ReleaseUnusedSourcesAsync(CancellationToken ct)
        {
            if (!_registry.AnySourceHasCachedData) return;

            var inUse = await _requests.GetSourceNamesInUseAsync(ct);
            _registry.ReleaseUnused(inUse);
        }
    }
}
