using dotAPNS;
using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Infrastructure.Notifications;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.DataSources.Probes
{
    /// <summary>
    /// Opens a connection to the application database. Not reached over HTTP, so — unlike the probes
    /// beside it — this one records its own observation.
    /// </summary>
    public sealed class PostgresProbe(
        FlightAssistantDbContext db,
        IDataSourceObserver observer,
        TimeProvider timeProvider) : IDataSourceProbe
    {
        public string Key => DataSourceKeys.Postgres;

        public async Task<DataSourceOutcome> ProbeAsync(CancellationToken ct)
        {
            DataSourceOutcome outcome;
            try
            {
                outcome = await db.Database.CanConnectAsync(ct) ? DataSourceOutcome.Ok : DataSourceOutcome.Failed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = DataSourceOutcome.Failed;
            }

            // The category only: a connection exception's message names the host and port.
            observer.Record(Key, outcome, timeProvider.GetUtcNow(),
                outcome == DataSourceOutcome.Ok ? null : "The database could not be reached");
            return outcome;
        }
    }

    /// <summary>
    /// Writes a short-lived key and reads it back — a round trip, not just a connection, since a
    /// Redis that accepts connections but rejects writes is not a working cache.
    ///
    /// <para>
    /// Takes <see cref="IDistributedCache"/> rather than the application's <c>ICacheService</c>:
    /// that resolves to an in-memory cache when no Redis is configured, and a probe of "redis" that
    /// silently checked a dictionary would be exactly the false green this feature exists to
    /// remove. The connection string decides instead, and NotConfigured is the honest answer.
    /// </para>
    /// </summary>
    public sealed class RedisProbe(
        IServiceProvider services,
        IConfiguration configuration,
        IDataSourceObserver observer,
        TimeProvider timeProvider) : IDataSourceProbe
    {
        private const string ProbeKey = "health:probe";

        public string Key => DataSourceKeys.Redis;

        public async Task<DataSourceOutcome> ProbeAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Redis")))
            {
                Record(DataSourceOutcome.NotConfigured);
                return DataSourceOutcome.NotConfigured;
            }

            var cache = services.GetService(typeof(IDistributedCache)) as IDistributedCache;
            if (cache is null)
            {
                Record(DataSourceOutcome.NotConfigured);
                return DataSourceOutcome.NotConfigured;
            }

            var written = Guid.NewGuid().ToString("N");

            try
            {
                await cache.SetStringAsync(
                    ProbeKey, written,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10) },
                    ct);

                var read = await cache.GetStringAsync(ProbeKey, ct);
                var outcome = read == written ? DataSourceOutcome.Ok : DataSourceOutcome.Failed;
                Record(outcome);
                return outcome;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Record(DataSourceOutcome.Failed);
                return DataSourceOutcome.Failed;
            }
        }

        private void Record(DataSourceOutcome outcome) =>
            observer.Record(Key, outcome, timeProvider.GetUtcNow(),
                outcome == DataSourceOutcome.Ok ? null : "The Redis cache could not be reached");
    }

    /// <summary>
    /// Signs a provider token with the configured P8 key and sends nothing. That is as far as a
    /// free check can go: Apple has no "verify my credentials" endpoint, and pushing to a real
    /// device token to find out would be a notification a traveler actually receives.
    /// </summary>
    public sealed class ApnsProbe(
        IOptions<ApnsOptions> options,
        IDataSourceObserver observer,
        TimeProvider timeProvider) : IDataSourceProbe
    {
        public string Key => DataSourceKeys.Apns;

        public Task<DataSourceOutcome> ProbeAsync(CancellationToken ct)
        {
            var opts = options.Value;

            if (string.IsNullOrWhiteSpace(opts.BundleId) || string.IsNullOrWhiteSpace(opts.KeyId)
                || string.IsNullOrWhiteSpace(opts.TeamId) || string.IsNullOrWhiteSpace(opts.P8KeyContent))
            {
                return Record(DataSourceOutcome.NotConfigured, "APNs credentials are not configured");
            }

            try
            {
                // Constructing the client is what parses and signs with the key; a malformed or
                // wrong-format P8 throws here, which is the failure worth catching before a push does.
                using var http = new HttpClient();
                ApnsClient.CreateUsingJwt(http, new ApnsJwtOptions
                {
                    BundleId = opts.BundleId,
                    KeyId = opts.KeyId,
                    TeamId = opts.TeamId,
                    CertContent = opts.P8KeyContent,
                });

                return Record(DataSourceOutcome.Ok, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The category only: the library's message can quote the key material it failed on.
                return Record(DataSourceOutcome.Unauthorized, "The APNs signing key could not be used");
            }
        }

        private Task<DataSourceOutcome> Record(DataSourceOutcome outcome, string? detail)
        {
            observer.Record(Key, outcome, timeProvider.GetUtcNow(), detail);
            return Task.FromResult(outcome);
        }
    }
}
