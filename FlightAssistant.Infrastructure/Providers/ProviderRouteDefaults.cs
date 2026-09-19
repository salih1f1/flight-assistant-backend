using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Providers;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Providers
{
    /// <inheritdoc/>
    public sealed class ProviderRouteDefaults : IProviderRouteDefaults
    {
        private readonly IOptions<AeroDataBoxOptions> _aeroDataBox;

        public ProviderRouteDefaults(IOptions<AeroDataBoxOptions> aeroDataBox) => _aeroDataBox = aeroDataBox;

        public IReadOnlyList<string> For(string job) => job switch
        {
            // Exactly the pre-#531 behaviour: route search went to AeroDataBox when its key was
            // configured and to AeroAPI otherwise. With no row, nothing changes.
            ProviderJobs.FlightSearchRoute =>
                _aeroDataBox.Value.IsConfigured ? [ProviderJobs.AeroDataBox] : [ProviderJobs.AeroApi],
            _ => throw new InvalidOperationException($"'{job}' is not a known provider routing key.")
        };

        public bool IsCeilinged(string job) => job switch
        {
            ProviderJobs.FlightSearchRoute => !_aeroDataBox.Value.IsConfigured,
            _ => throw new InvalidOperationException($"'{job}' is not a known provider routing key.")
        };
    }
}
