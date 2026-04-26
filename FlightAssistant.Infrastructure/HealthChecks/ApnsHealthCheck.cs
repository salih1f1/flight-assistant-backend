using FlightAssistant.Infrastructure.Notifications;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.HealthChecks
{
    public sealed class ApnsHealthCheck : IHealthCheck
    {
        private readonly ApnsOptions _options;
        private readonly bool _isDevelopment;

        public ApnsHealthCheck(IOptions<ApnsOptions> options, IHostEnvironment env)
        {
            _options = options.Value;
            _isDevelopment = env.IsDevelopment();
        }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken ct = default)
        {
            if (_isDevelopment)
                return Task.FromResult(HealthCheckResult.Healthy("APNs credentials not required in Development."));

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(_options.BundleId))  missing.Add(nameof(ApnsOptions.BundleId));
            if (string.IsNullOrWhiteSpace(_options.KeyId))     missing.Add(nameof(ApnsOptions.KeyId));
            if (string.IsNullOrWhiteSpace(_options.TeamId))    missing.Add(nameof(ApnsOptions.TeamId));
            if (string.IsNullOrWhiteSpace(_options.P8KeyContent)) missing.Add(nameof(ApnsOptions.P8KeyContent));

            return missing.Count == 0
                ? Task.FromResult(HealthCheckResult.Healthy("APNs credentials present."))
                : Task.FromResult(HealthCheckResult.Degraded($"Missing APNs config: {string.Join(", ", missing)}"));
        }
    }
}
