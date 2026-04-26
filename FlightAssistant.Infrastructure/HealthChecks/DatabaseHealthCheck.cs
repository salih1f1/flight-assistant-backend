using FlightAssistant.Infrastructure.Persistance;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlightAssistant.Infrastructure.HealthChecks
{
    public sealed class DatabaseHealthCheck : IHealthCheck
    {
        private readonly FlightAssistantDbContext _db;

        public DatabaseHealthCheck(FlightAssistantDbContext db) => _db = db;

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken ct = default)
        {
            try
            {
                return await _db.Database.CanConnectAsync(ct)
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy("Cannot connect to the database.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Database check threw an exception.", ex);
            }
        }
    }
}
