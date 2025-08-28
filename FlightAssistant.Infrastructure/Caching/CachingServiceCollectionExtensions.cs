using FlightAssistant.Application.Interfaces.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.Infrastructure.Caching
{
    public static class CachingServiceCollectionExtensions
    {
        public static IServiceCollection AddAppCaching(this IServiceCollection services, IConfiguration configuration)
        {
            var redisConn = configuration.GetConnectionString("Redis");
            if (!string.IsNullOrWhiteSpace(redisConn))
            {
                services.AddStackExchangeRedisCache(opt => opt.Configuration = redisConn);
                services.AddSingleton<ICacheService, RedisCacheService>();
            }
            else
            {
                services.AddMemoryCache();
                services.AddSingleton<ICacheService, MemoryCacheService>();
            }

            return services;
        }
    }
}
