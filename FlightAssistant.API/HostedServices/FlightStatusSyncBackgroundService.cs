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

        public FlightStatusSyncBackgroundService(
            IServiceScopeFactory scopeFactory,
            IConfiguration config,
            ILogger<FlightStatusSyncBackgroundService> log)
        {
            _scopeFactory = scopeFactory;
            _config = config;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_config.GetValue("FlightSync:Enabled", true))
            {
                _log.LogInformation("FlightStatusSyncBackgroundService is disabled via config.");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan delay;
                try
                {
                    delay = await RunBatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    var retryIn = _config.GetValue("FlightSync:Interval", TimeSpan.FromMinutes(10));
                    _log.LogError(ex, "Flight status sync batch failed. Retrying in {Interval}.", retryIn);
                    delay = retryIn;
                }

                await Task.Delay(delay, stoppingToken);
            }
        }

        private async Task<TimeSpan> RunBatchAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var flightRepo = scope.ServiceProvider.GetRequiredService<IFlightRepository>();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            var flights = await flightRepo.GetActiveTrackedFlightsAsync(ct);

            var now = DateTime.UtcNow;
            var hasNearDeparture = false;

            foreach (var flight in flights)
            {
                try
                {
                    await mediator.Send(new SyncFlightStatusCommand(null, flight.Id), ct);
                    _log.LogInformation("Synced flight {FlightId} ({FlightNumber})", flight.Id, flight.FlightNumber);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to sync flight {FlightId}", flight.Id);
                }

                if (IsActiveForFastPoll(flight.ScheduledDepartureUtc, flight.ScheduledArrivalUtc, flight.ActualArrivalUtc, now))
                    hasNearDeparture = true;
            }

            var normalInterval = _config.GetValue("FlightSync:Interval", TimeSpan.FromMinutes(10));
            var nearDeptInterval = _config.GetValue("FlightSync:NearDepartureInterval", TimeSpan.FromMinutes(2));
            return hasNearDeparture ? nearDeptInterval : normalInterval;
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
