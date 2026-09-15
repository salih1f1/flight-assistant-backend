using FlightAssistant.Application.FlightSync;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.HostedServices
{
    public static class FlightSyncServiceCollectionExtensions
    {
        /// <summary>The status sync: its options (validated on start), their Application-layer view,
        /// the heartbeat and the background service.</summary>
        public static IServiceCollection AddFlightSync(this IServiceCollection services)
        {
            services.AddOptions<FlightSyncOptions>()
                .BindConfiguration(FlightSyncOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Application can't reference this API-layer class (the IFlightSearchHorizonOptions pattern).
            services.AddSingleton<IFlightSyncOptions>(sp =>
                sp.GetRequiredService<IOptions<FlightSyncOptions>>().Value);

            services.AddSingleton<FlightSyncHeartbeat>();
            services.AddHostedService<FlightStatusSyncBackgroundService>();
            return services;
        }
    }
}
