using FlightAssistant.Application.ReferenceData;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.Infrastructure
{
    public static class ReferenceProvisioningServiceCollectionExtensions
    {
        /// <summary>Binds and validates <see cref="ReferenceProvisioningOptions"/> and exposes it to
        /// Application as <see cref="IReferenceProvisioningOptions"/> (#465).</summary>
        public static IServiceCollection AddReferenceProvisioning(this IServiceCollection services)
        {
            services.AddOptions<ReferenceProvisioningOptions>()
                .BindConfiguration(ReferenceProvisioningOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddSingleton<IReferenceProvisioningOptions>(sp =>
                sp.GetRequiredService<IOptions<ReferenceProvisioningOptions>>().Value);

            return services;
        }
    }
}
