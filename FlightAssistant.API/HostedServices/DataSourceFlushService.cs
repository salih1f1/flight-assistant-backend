using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Infrastructure.DataSources;

namespace FlightAssistant.API.HostedServices
{
    /// <summary>
    /// Drains <see cref="BufferedDataSourceObserver"/> into <c>data_source_status</c> on a timer,
    /// and once more on graceful shutdown so a clean restart loses nothing.
    ///
    /// <para>
    /// The observer is a singleton and the repository is scoped, so each flush opens its own scope
    /// — the same shape the verification job uses. A flush that throws is logged and swallowed:
    /// losing a diagnostics write must never take the host down.
    /// </para>
    /// </summary>
    public sealed class DataSourceFlushService : BackgroundService
    {
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

        private readonly BufferedDataSourceObserver _observer;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DataSourceFlushService> _logger;

        public DataSourceFlushService(
            BufferedDataSourceObserver observer,
            IServiceScopeFactory scopeFactory,
            ILogger<DataSourceFlushService> logger)
        {
            _observer = observer;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(FlushInterval);

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                    await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutting down: fall through to the final flush below.
            }

            // CancellationToken.None deliberately — this flush runs *because* the host is stopping,
            // so it must not be cancelled by the token that signalled it.
            await FlushAsync(CancellationToken.None);
        }

        private async Task FlushAsync(CancellationToken ct)
        {
            try
            {
                var pending = _observer.TakePending();
                if (pending.Count == 0) return;

                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IDataSourceStatusRepository>();

                foreach (var row in pending)
                    await repo.UpsertAsync(row, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Failed to flush data source status rows.");
            }
        }
    }
}
