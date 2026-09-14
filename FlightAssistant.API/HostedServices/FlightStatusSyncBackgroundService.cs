using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.API.HostedServices
{
    public class FlightStatusSyncBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<FlightStatusSyncBackgroundService> _log;
        private readonly FlightSyncHeartbeat _heartbeat;

        public FlightStatusSyncBackgroundService(
            IServiceScopeFactory scopeFactory,
            IConfiguration config,
            ILogger<FlightStatusSyncBackgroundService> log,
            FlightSyncHeartbeat heartbeat)
        {
            _scopeFactory = scopeFactory;
            _config = config;
            _log = log;
            _heartbeat = heartbeat;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_config.GetValue("FlightSync:Enabled", true))
            {
                _log.LogInformation("FlightStatusSyncBackgroundService is disabled via config.");
                // Heartbeat is left untouched: it must stay NeverRun so the admin health endpoint
                // can render "disabled" rather than "broken".
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                _heartbeat.MarkRunStarted();

                TimeSpan delay;
                try
                {
                    var result = await RunBatchAsync(stoppingToken);
                    delay = result.Delay;
                    _heartbeat.MarkRunSucceeded(result.Processed, result.Failed, DateTime.UtcNow + delay);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Clean shutdown, not a failed run — heartbeat is left as-is.
                    break;
                }
                catch (Exception ex)
                {
                    var retryIn = _config.GetValue("FlightSync:Interval", TimeSpan.FromMinutes(10));
                    _log.LogError(ex, "Flight status sync batch failed. Retrying in {Interval}.", retryIn);
                    _heartbeat.MarkRunFailed(ex, DateTime.UtcNow + retryIn);
                    delay = retryIn;
                }

                await Task.Delay(delay, stoppingToken);
            }
        }

        /// <summary>Outcome of a single sync batch: the delay before the next run, how many flights
        /// the batch worked through, and how many of those threw in the per-flight
        /// catch-and-continue loop.</summary>
        private record BatchResult(TimeSpan Delay, int Processed, int Failed);

        private async Task<BatchResult> RunBatchAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var flightRepo = scope.ServiceProvider.GetRequiredService<IFlightRepository>();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var flights = await flightRepo.GetActiveTrackedFlightsAsync(ct);

            var now = DateTime.UtcNow;
            var hasNearDeparture = false;
            var failed = 0;

            foreach (var flight in flights)
            {
                try
                {
                    await mediator.Send(new SyncFlightStatusCommand(null, flight.Id, SyncTrigger.Scheduled), ct);
                    _log.LogInformation("Synced flight {FlightId} ({FlightNumber})", flight.Id, flight.FlightNumber);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to sync flight {FlightId}", flight.Id);
                    failed++;
                }

                if (IsActiveForFastPoll(flight.ScheduledDepartureUtc, flight.ScheduledArrivalUtc, flight.ActualArrivalUtc, now))
                    hasNearDeparture = true;
            }

            var normalInterval = _config.GetValue("FlightSync:Interval", TimeSpan.FromMinutes(10));
            var nearDeptInterval = _config.GetValue("FlightSync:NearDepartureInterval", TimeSpan.FromMinutes(2));
            var delay = hasNearDeparture ? nearDeptInterval : normalInterval;
            return new BatchResult(delay, flights.Count, failed);
        }

        /// <summary>
        /// Determines whether a flight is within the genuinely-active fast-poll window:
        /// from 4 hours before scheduled departure THROUGH arrival. Bounds the window so
        /// flights that have already arrived no longer keep the service in the fast-poll
        /// cycle (see #242).
        /// </summary>
        public static bool IsActiveForFastPoll(
            DateTime scheduledDepartureUtc,
            DateTime scheduledArrivalUtc,
            DateTime? actualArrivalUtc,
            DateTime now)
        {
            var hoursToDeparture = (scheduledDepartureUtc - now).TotalHours;
            var notYetArrived = (actualArrivalUtc ?? scheduledArrivalUtc) > now;
            return hoursToDeparture <= 4 && notYetArrived;
        }
    }
}
