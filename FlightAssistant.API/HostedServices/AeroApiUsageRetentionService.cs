using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.HostedServices
{
    /// <summary>
    /// Prunes <c>aeroapi_calls</c> rows older than <see cref="AeroApiOptions.UsageRetentionDays"/>
    /// (default 90) so the per-call usage-metering table (Task 2, issue #293) does not grow
    /// unbounded. Runs once on start and once a day thereafter.
    ///
    /// <para>
    /// A hosted service is a singleton, so the scoped <see cref="FlightAssistantDbContext"/> is
    /// resolved per pass via <see cref="IServiceScopeFactory"/> rather than injected directly.
    /// <see cref="TimeProvider"/> stands in for <see cref="DateTime.UtcNow"/> and for the daily
    /// wait, so both the cutoff and the schedule are testable on a fake clock without any real
    /// sleeping.
    /// </para>
    /// </summary>
    public sealed class AeroApiUsageRetentionService : BackgroundService
    {
        private static readonly TimeSpan PruneInterval = TimeSpan.FromDays(1);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<AeroApiUsageRetentionService> _log;

        public AeroApiUsageRetentionService(
            IServiceScopeFactory scopeFactory,
            TimeProvider timeProvider,
            ILogger<AeroApiUsageRetentionService> log)
        {
            _scopeFactory = scopeFactory;
            _timeProvider = timeProvider;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PruneOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(PruneInterval, _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// One prune pass: deletes every <c>aeroapi_calls</c> row older than
        /// <see cref="AeroApiOptions.UsageRetentionDays"/>, entirely server-side via
        /// <c>ExecuteDeleteAsync</c> — rows are never loaded into memory just to be deleted, which
        /// matters once the table has grown past the retention window. Public so tests can drive one
        /// pass directly rather than through the infinite <see cref="ExecuteAsync"/> loop. A genuine
        /// failure (DB unreachable, etc.) is logged and swallowed — returning 0 rather than
        /// throwing — so it is left for the next scheduled cycle to retry and can never crash the
        /// host; a cancellation requested on <paramref name="ct"/> propagates instead, so
        /// <see cref="ExecuteAsync"/> can tell a real failure apart from an ordinary shutdown.
        /// </summary>
        public async Task<int> PruneOnceAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
                var options = scope.ServiceProvider.GetRequiredService<IOptions<AeroApiOptions>>().Value;

                var cutoffUtc = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-options.UsageRetentionDays);

                var deleted = await db.AeroApiCalls
                    .Where(c => c.CalledUtc < cutoffUtc)
                    .ExecuteDeleteAsync(ct);

                if (deleted > 0)
                    _log.LogInformation(
                        "AeroAPI usage retention pruned {Count} aeroapi_calls row(s) older than {CutoffUtc:O}.",
                        deleted, cutoffUtc);

                return deleted;
            }
            catch (Exception ex) when (ct.IsCancellationRequested is false)
            {
                _log.LogError(ex, "AeroAPI usage retention prune failed; will retry on the next cycle.");
                return 0;
            }
        }
    }
}
