using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Jobs;
using FlightAssistant.Application.Jobs;
using FlightAssistant.Application.Verification;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.HostedServices
{
    /// <summary>
    /// The reference-data verification job (#367, spec §4.2), following
    /// <see cref="FlightStatusSyncBackgroundService"/>. This loop only waits
    /// <see cref="VerificationOptions.PollInterval"/>, creates each cycle's DI scope with the system
    /// actor on its <see cref="AdminContext"/>, calls <see cref="IVerificationCycleRunner"/> and
    /// updates <see cref="VerificationJobHeartbeat"/>. The cycle itself (reset, claim, one scope per
    /// request, release) lives in the Application runner, so it is tested against PostgreSQL without
    /// this project.
    ///
    /// <para>
    /// <b>A cycle that paused (#401 task 2, R4/R5).</b> <see cref="VerificationCycleResult.Paused"/>
    /// &gt; 0 means the batch ran to completion but left some requests Pending because this month's
    /// AeroAPI budget is exhausted -- <see cref="VerificationJobHeartbeat.MarkRunPaused"/>, not
    /// <see cref="VerificationJobHeartbeat.MarkRunSucceeded"/>, so <c>/admin/health</c> shows this
    /// distinctly from an ordinary success. It is still not a failure: a
    /// <see cref="VerificationCycleFailedException"/> after the batch reports the failed batch's own
    /// <c>Paused</c> count alongside its other counts, since a cycle can pause some requests and then
    /// separately fail releasing source caches.
    /// </para>
    /// </summary>
    public sealed class ReferenceVerificationBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly VerificationOptions _options;
        private readonly VerificationJobHeartbeat _heartbeat;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<ReferenceVerificationBackgroundService> _log;
        private readonly IJobSwitch _jobSwitch;

        public ReferenceVerificationBackgroundService(
            IServiceScopeFactory scopeFactory,
            IOptions<VerificationOptions> options,
            VerificationJobHeartbeat heartbeat,
            TimeProvider timeProvider,
            ILogger<ReferenceVerificationBackgroundService> log,
            IJobSwitch jobSwitch)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _heartbeat = heartbeat;
            _timeProvider = timeProvider;
            _log = log;
            _jobSwitch = jobSwitch;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _log.LogInformation("ReferenceVerificationBackgroundService is disabled via config.");
                // The heartbeat stays NeverRun, so the admin health report reads "disabled", not "broken".
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                // Read per tick, before MarkRunStarted: this tick may do nothing, and marking a run
                // started before knowing that would put a run in the heartbeat that never happened.
                if (!await _jobSwitch.IsEnabledAsync(JobNames.Verification, stoppingToken))
                {
                    _heartbeat.MarkSwitchedOff();
                    await DelayUntilNextCycleAsync(stoppingToken);
                    continue;
                }

                _heartbeat.MarkRunStarted();

                try
                {
                    var result = await RunCycleAsync(stoppingToken);
                    if (result.Paused > 0)
                        _heartbeat.MarkRunPaused(
                            result.RequestsProcessed, result.RequestsFailed, result.StuckAbandoned, result.FailuresUnrecorded,
                            result.Paused, NextRunDueUtc());
                    else
                        _heartbeat.MarkRunSucceeded(
                            result.RequestsProcessed, result.RequestsFailed, result.StuckAbandoned, result.FailuresUnrecorded, NextRunDueUtc());
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Clean shutdown, not a failed cycle.
                    break;
                }
                catch (VerificationCycleFailedException ex)
                {
                    // The batch was processed before the failure: report its counts, and its cause.
                    var cause = ex.InnerException ?? ex;
                    _log.LogError(cause,
                        "Reference verification cycle failed after processing {Processed} request(s). Retrying in {PollInterval}.",
                        ex.CompletedWork.RequestsProcessed, _options.PollInterval);
                    _heartbeat.MarkRunFailed(
                        cause, NextRunDueUtc(),
                        ex.CompletedWork.RequestsProcessed, ex.CompletedWork.RequestsFailed,
                        ex.CompletedWork.StuckAbandoned, ex.CompletedWork.FailuresUnrecorded, ex.CompletedWork.Paused);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Reference verification cycle failed. Retrying in {PollInterval}.", _options.PollInterval);
                    _heartbeat.MarkRunFailed(ex, NextRunDueUtc());
                }

                await DelayUntilNextCycleAsync(stoppingToken);
            }
        }

        /// <summary>
        /// The loop's wait between cycles -- factored out (#471) so the switched-off branch above
        /// polls the switch at <see cref="VerificationOptions.PollInterval"/> too, rather than
        /// spinning or introducing a second waiting mechanism. Cancellation during the wait is a
        /// clean shutdown: swallowing it here has the same effect as the explicit <c>break</c> this
        /// replaced, since the while loop's own condition ends the loop on its next check.
        /// </summary>
        private async Task DelayUntilNextCycleAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Clean shutdown, not a failed cycle -- same reasoning as the RunCycleAsync catch above.
            }
        }

        private async Task<VerificationCycleResult> RunCycleAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            VerificationJobActor.ApplyTo(scope.ServiceProvider.GetRequiredService<AdminContext>());

            return await scope.ServiceProvider
                .GetRequiredService<IVerificationCycleRunner>()
                .RunCycleAsync(ct);
        }

        private DateTime NextRunDueUtc() => _timeProvider.GetUtcNow().UtcDateTime + _options.PollInterval;
    }
}
