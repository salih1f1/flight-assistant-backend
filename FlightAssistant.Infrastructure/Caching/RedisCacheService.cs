using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Infrastructure.DataSources;

namespace FlightAssistant.Infrastructure.Caching
{
    /// <summary>
    /// Redis-backed cache, registered only when a Redis connection string is configured — so every
    /// observation it records (#460) really is about Redis, never about the in-memory fallback.
    /// Reads and writes are observed because Redis is not reached over HTTP; the caller's outcome is
    /// untouched, a failure still propagating exactly as before.
    /// </summary>
    internal sealed class RedisCacheService : ICacheService
    {
        private readonly IDistributedCache _cache;
        private readonly IDataSourceObserver _observer;
        private readonly TimeProvider _timeProvider;
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

        private const string FailureDetail = "The Redis cache could not be reached";

        public RedisCacheService(IDistributedCache cache, IDataSourceObserver observer, TimeProvider timeProvider)
        {
            _cache = cache;
            _observer = observer;
            _timeProvider = timeProvider;
        }

        public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            byte[]? data;
            try
            {
                data = await _cache.GetAsync(key, ct);
            }
            catch (Exception)
            {
                // The category only: a StackExchange.Redis message names the endpoint.
                Record(DataSourceOutcome.Failed);
                throw;
            }

            Record(DataSourceOutcome.Ok);

            // Deserialization failures are this application's problem, not Redis's, so they are
            // recorded as neither — the read above already succeeded.
            if (data is null || data.Length == 0) return default;
            return JsonSerializer.Deserialize<T>(data, SerializerOptions);
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            var options = new DistributedCacheEntryOptions();
            if (absoluteExpire.HasValue)
                options.AbsoluteExpirationRelativeToNow = absoluteExpire;

            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);

            try
            {
                await _cache.SetAsync(key, bytes, options, ct);
            }
            catch (Exception)
            {
                Record(DataSourceOutcome.Failed);
                throw;
            }

            Record(DataSourceOutcome.Ok);
        }

        public Task RemoveAsync(string key, CancellationToken ct = default) => _cache.RemoveAsync(key, ct);

        private void Record(DataSourceOutcome outcome)
        {
            try
            {
                _observer.Record(
                    DataSourceKeys.Redis,
                    outcome,
                    _timeProvider.GetUtcNow(),
                    outcome == DataSourceOutcome.Ok ? null : FailureDetail);
            }
            catch
            {
                // Never fail a cache operation because diagnostics could not be written.
            }
        }
    }
}
