using System;
using System.Threading;
using System.Threading.Tasks;

namespace FlightAssistant.Application.Interfaces.Caching
{
    public interface ICacheService
    {
        Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
        Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default);
        Task RemoveAsync(string key, CancellationToken ct = default);
        Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? absoluteExpire = null, CancellationToken ct = default);
    }
}
