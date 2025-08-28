using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using FlightAssistant.Application.Interfaces.Caching;

namespace FlightAssistant.Infrastructure.Caching
{
    internal sealed class RedisCacheService : ICacheService
    {
        private readonly IDistributedCache _cache;
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

        public RedisCacheService(IDistributedCache cache)
        {
            _cache = cache;
        }

        public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            var data = await _cache.GetAsync(key, ct);
            if (data is null || data.Length == 0) return default;
            return JsonSerializer.Deserialize<T>(data, SerializerOptions);
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            var options = new DistributedCacheEntryOptions();
            if (absoluteExpire.HasValue)
                options.AbsoluteExpirationRelativeToNow = absoluteExpire;

            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
            await _cache.SetAsync(key, bytes, options, ct);
        }

        public Task RemoveAsync(string key, CancellationToken ct = default) => _cache.RemoveAsync(key, ct);

        public async Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            var cached = await GetAsync<T>(key, ct);
            if (!Equals(cached, default(T)))
            {
                return cached!;
            }

            var value = await factory(ct);
            await SetAsync(key, value, absoluteExpire, ct);
            return value;
        }
    }
}
