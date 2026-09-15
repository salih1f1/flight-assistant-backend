using FlightAssistant.Application.Administration;
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
    /// </summary>
    public sealed class ReferenceVerificationBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly VerificationOptions _options;
        private readonly VerificationJobHeartbeat _heartbeat;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<ReferenceVerificationBackgroundService> _log;

        public ReferenceVerificationBackgroundService(
            IServiceScopeFactory scopeFactory,
            IOptions<VerificationOptions> options,
            VerificationJobHeartbeat heartbeat,
            TimeProvider timeProvider,
            ILogger<ReferenceVerificationBackgroundService> log)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _heartbeat = heartbeat;
            _timeProvider = timeProvider;
            _log = log;
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
                _heartbeat.MarkRunStarted();

                try
                {
                    var result = await RunCycleAsync(stoppingToken);
                    _heartbeat.MarkRunSucceeded(result.RequestsProcessed, result.RequestsFailed, NextRunDueUtc());
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
                    _heartbeat.MarkRunFailed(cause, NextRunDueUtc(), ex.CompletedWork.RequestsProcessed, ex.CompletedWork.RequestsFailed);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Reference verification cycle failed. Retrying in {PollInterval}.", _options.PollInterval);
                    _heartbeat.MarkRunFailed(ex, NextRunDueUtc());
                }

                try
                {
                    await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
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
