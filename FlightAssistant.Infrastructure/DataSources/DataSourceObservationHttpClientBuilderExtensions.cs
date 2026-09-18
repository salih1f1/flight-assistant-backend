using FlightAssistant.Application.Interfaces.DataSources;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.Infrastructure.DataSources
{
    /// <summary>
    /// Attaches a <see cref="DataSourceObservingHandler"/> to a named client, carrying that
    /// client's catalog key. An extension rather than a helper local to
    /// <c>AddInfrastructure</c> so each registration reads as one chain and the key sits beside
    /// the client it belongs to.
    /// </summary>
    public static class DataSourceObservationHttpClientBuilderExtensions
    {
        /// <summary>
        /// Call this LAST on a builder. Handlers run outermost-first in registration order, so
        /// adding it last makes it innermost: it then sees the transport result of each individual
        /// attempt, rather than the resilience pipeline's verdict after its retries.
        /// </summary>
        public static IHttpClientBuilder AddDataSourceObservation(this IHttpClientBuilder builder, string sourceKey) =>
            builder.AddHttpMessageHandler(sp => new DataSourceObservingHandler(
                sp.GetRequiredService<IDataSourceObserver>(),
                sp.GetRequiredService<TimeProvider>())
            {
                SourceKey = sourceKey,
            });
    }
}
