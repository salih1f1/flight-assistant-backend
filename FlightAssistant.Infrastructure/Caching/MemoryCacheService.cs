using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using FlightAssistant.Application.Interfaces.Caching;

namespace FlightAssistant.Infrastructure.Caching
{
    internal sealed class MemoryCacheService : ICacheService
    {
        private readonly IMemoryCache _cache;

        public MemoryCacheService(IMemoryCache cache)
        {
            _cache = cache;
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            _cache.TryGetValue(key, out T? value);
            return Task.FromResult(value);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            var options = new MemoryCacheEntryOptions();
            if (absoluteExpire.HasValue)
                options.SetAbsoluteExpiration(absoluteExpire.Value);
            _cache.Set(key, value, options);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            _cache.Remove(key);
            return Task.CompletedTask;
        }

        public async Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            var existing = await GetAsync<T>(key, ct);
            if (!Equals(existing, default(T)))
            {
                return existing!;
            }

            var value = await factory(ct);
            await SetAsync(key, value, absoluteExpire, ct);
            return value;
        }
    }
}
