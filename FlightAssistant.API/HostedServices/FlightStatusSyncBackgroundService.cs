using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Interfaces.Jobs;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Jobs;
using MediatR;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.HostedServices
{
    /// <summary>
    /// Syncs tracked flights whose <c>NextSyncDueUtc</c> has passed, oldest due first (#356, spec
    /// §2.2). Each flight's own sync mode sets when it's next due, so a flight near departure no
    /// longer speeds up every other flight (#344), and a finished or not-yet-answerable flight makes
    /// no call (#343, #345).
    /// </summary>
    public class FlightStatusSyncBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly FlightSyncOptions _options;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<FlightStatusSyncBackgroundService> _log;
        private readonly FlightSyncHeartbeat _heartbeat;
        private readonly IJobSwitch _jobSwitch;

        public FlightStatusSyncBackgroundService(
            IServiceScopeFactory scopeFactory,
            IOptions<FlightSyncOptions> options,
            TimeProvider timeProvider,
            ILogger<FlightStatusSyncBackgroundService> log,
            FlightSyncHeartbeat heartbeat,
            IJobSwitch jobSwitch)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _timeProvider = timeProvider;
            _log = log;
            _heartbeat = heartbeat;
            _jobSwitch = jobSwitch;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Unchanged (#471): the config ceiling still terminates the service. Nothing reachable
            // from the admin panel can lift it, so there is no reason to keep a loop alive for it —
            // and the heartbeat still stays NeverRun, which is what makes /admin/health say
            // "disabled" here rather than "broken".
            if (!_options.Enabled)
            {
                _log.LogInformation("FlightStatusSyncBackgroundService is disabled via config.");
                return;
            }

            // PeriodicTimer doesn't queue missed ticks: a run longer than the interval is followed
            // straight away by the next one. Due flights are taken oldest first, so none is skipped.
            using var timer = new PeriodicTimer(_options.LoopInterval, _timeProvider);
            try
            {
                do
                {
                    // Read per tick, not once: the switch is what an admin can change while this
                    // loop is running, and a loop that read it once could never be switched back on.
                    if (!await _jobSwitch.IsEnabledAsync(JobNames.FlightSync, stoppingToken))
                    {
                        _heartbeat.MarkSwitchedOff();
                        continue;
                    }

                    await RunAndRecordAsync(stoppingToken);
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Clean shutdown, not a failed run — heartbeat is left as-is.
            }
        }

        private async Task RunAndRecordAsync(CancellationToken ct)
        {
            _heartbeat.MarkRunStarted();
            var startedUtc = _timeProvider.GetUtcNow();
            var startTimestamp = _timeProvider.GetTimestamp();
            var nextRunDueUtc = startedUtc.UtcDateTime + _options.LoopInterval;

            try
            {
                var result = await RunOnceAsync(ct);
                _heartbeat.MarkRunSucceeded(result.Processed, result.Failed, nextRunDueUtc, MeasureAndWarn(startTimestamp));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Flight status sync run failed. Retrying in {Interval}.", _options.LoopInterval);
                _heartbeat.MarkRunFailed(ex, nextRunDueUtc, MeasureAndWarn(startTimestamp));
            }
        }

        /// <summary>
        /// How long the run took, by the injected clock's monotonic timestamp rather than a wall-clock
        /// difference, so an NTP step mid-run cannot corrupt the figure or trip the warning below (#353).
        /// A run longer than the loop interval is not an error — <c>PeriodicTimer</c> simply starts the next
        /// one immediately — but it means each flight is polled less often than the interval suggests,
        /// which is invisible without this warning.
        /// </summary>
        private TimeSpan MeasureAndWarn(long startTimestamp)
        {
            var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
            if (elapsed > _options.LoopInterval)
                _log.LogWarning(
                    "Flight status sync run took {Duration}, longer than the {Interval} loop interval; the next run starts immediately.",
                    elapsed, _options.LoopInterval);

            return elapsed;
        }

        /// <summary>
        /// One pass over the due flights. Each flight syncs in its own DI scope, so a flight that
        /// fails part-way can't have its changes saved by the next flight's <c>SaveChanges</c>.
        /// </summary>
        public async Task<FlightSyncRunResult> RunOnceAsync(CancellationToken ct)
        {
            IReadOnlyList<int> flightIds;
            using (var scope = _scopeFactory.CreateScope())
            {
                flightIds = await scope.ServiceProvider.GetRequiredService<IFlightRepository>()
                    .GetDueTrackedFlightIdsAsync(ct);
            }

            var failed = 0;
            foreach (var flightId in flightIds)
            {
                using var scope = _scopeFactory.CreateScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<IMediator>()
                        .Send(new SyncFlightStatusCommand(null, flightId, SyncTrigger.Scheduled), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // The handler moved its next sync one interval on, so it's retried at its mode's
                    // cadence, not on every loop.
                    _log.LogError(ex, "Failed to sync flight {FlightId}", flightId);
                    failed++;
                }
            }

            return new FlightSyncRunResult(flightIds.Count, failed);
        }
    }

    /// <summary>How many due flights a run sent, and how many of those threw.</summary>
    public sealed record FlightSyncRunResult(int Processed, int Failed);
}
