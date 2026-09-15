using FlightAssistant.Application.Verification;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.HostedServices
{
    public static class VerificationJobServiceCollectionExtensions
    {
        /// <summary>
        /// The reference-data verification job: its options (validated on start), their
        /// Application-layer view, the heartbeat and the background service.
        /// </summary>
        public static IServiceCollection AddVerificationJob(this IServiceCollection services)
        {
            services.AddOptions<VerificationOptions>()
                .BindConfiguration(VerificationOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Application can't reference this API-layer class (the IFlightSearchHorizonOptions pattern).
            services.AddSingleton<IVerificationJobOptions>(sp => sp.GetRequiredService<IOptions<VerificationOptions>>().Value);

            services.AddSingleton<VerificationJobHeartbeat>();
            services.AddHostedService<ReferenceVerificationBackgroundService>();

            return services;
        }
    }
}
