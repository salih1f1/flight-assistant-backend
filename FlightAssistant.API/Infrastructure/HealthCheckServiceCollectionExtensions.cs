using FlightAssistant.Infrastructure.HealthChecks;

namespace FlightAssistant.API.Infrastructure
{
    /// <summary>
    /// The application's health checks, in one place a test can read (#467).
    ///
    /// <para>
    /// There is deliberately no <c>aeroapi</c> check. The one that used to be here called every
    /// HTTP response Healthy — "Reachable — HTTP 401" included — because it sent a HEAD to the base
    /// URL on a bare client carrying no key, so a reachable host was all it could ever observe. The
    /// <c>aeroapi</c> data-source row answers that question properly now (#460): observed off the
    /// real, keyed calls, checked on demand by the free <c>account/usage</c> probe, and read through
    /// the one status evaluator. Do not add a live AeroAPI check back beside it — two rows
    /// disagreeing about the same key is the defect #467 closed.
    /// </para>
    /// </summary>
    public static class HealthCheckServiceCollectionExtensions
    {
        public static IServiceCollection AddAppHealthChecks(this IServiceCollection services, IConfiguration configuration)
        {
            var builder = services.AddHealthChecks()
                .AddCheck<DatabaseHealthCheck>("database")
                // 'external' is what /health/ready's predicate excludes: App Service restarts the
                // app on a 503, so an outside dependency must never be able to cause one.
                .AddCheck<ApnsHealthCheck>("apns", tags: ["external"]);

            var redisConnectionString = configuration.GetConnectionString("Redis");
            if (!string.IsNullOrWhiteSpace(redisConnectionString))
                builder.AddRedis(redisConnectionString);

            return services;
        }
    }
}
