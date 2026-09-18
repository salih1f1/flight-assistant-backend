using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Infrastructure.DataSources;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlightAssistant.Infrastructure.HealthChecks
{
    /// <summary>
    /// The database's liveness check, and — since the database is not reached over HTTP, so
    /// <see cref="DataSourceObservingHandler"/> cannot see it — the seam that records the
    /// <c>postgres</c> source's observations (#460). The <see cref="HealthCheckResult"/> it returns
    /// is unchanged by that: this observes the check, it does not decide it.
    /// </summary>
    public sealed class DatabaseHealthCheck : IHealthCheck
    {
        private readonly FlightAssistantDbContext _db;
        private readonly IDataSourceObserver _observer;
        private readonly TimeProvider _timeProvider;

        public DatabaseHealthCheck(FlightAssistantDbContext db, IDataSourceObserver observer, TimeProvider timeProvider)
        {
            _db = db;
            _observer = observer;
            _timeProvider = timeProvider;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken ct = default)
        {
            try
            {
                if (await _db.Database.CanConnectAsync(ct))
                {
                    Record(DataSourceOutcome.Ok, null);
                    return HealthCheckResult.Healthy();
                }

                Record(DataSourceOutcome.Failed, FailureDetail);
                return HealthCheckResult.Unhealthy("Cannot connect to the database.");
            }
            catch (Exception ex)
            {
                // The category only: a connection exception's message names the host and port.
                Record(DataSourceOutcome.Failed, FailureDetail);
                return HealthCheckResult.Unhealthy("Database check threw an exception.", ex);
            }
        }

        private const string FailureDetail = "The database could not be reached";

        private void Record(DataSourceOutcome outcome, string? detail)
        {
            try
            {
                _observer.Record(DataSourceKeys.Postgres, outcome, _timeProvider.GetUtcNow(), detail);
            }
            catch
            {
                // Diagnostics must never change the health check's answer.
            }
        }
    }
}
