using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Providers;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Providers
{
    /// <inheritdoc/>
    public sealed class ProviderRouteDefaults : IProviderRouteDefaults
    {
        private readonly IOptions<AeroDataBoxOptions> _aeroDataBox;
        private readonly IVerificationJobOptions _verificationOptions;

        public ProviderRouteDefaults(IOptions<AeroDataBoxOptions> aeroDataBox, IVerificationJobOptions verificationOptions)
        {
            _aeroDataBox = aeroDataBox;
            _verificationOptions = verificationOptions;
        }

        public IReadOnlyList<string> For(string job) => job switch
        {
            // Exactly the pre-#531 behaviour: route search went to AeroDataBox when its key was
            // configured and to AeroAPI otherwise. With no row, nothing changes.
            ProviderJobs.FlightSearchRoute =>
                _aeroDataBox.Value.IsConfigured ? [ProviderJobs.AeroDataBox] : [ProviderJobs.AeroApi],
            ProviderJobs.VerificationAirport =>
                _verificationOptions.DefaultSourcesFor(VerificationEntityType.Airport),
            ProviderJobs.VerificationAirline =>
                _verificationOptions.DefaultSourcesFor(VerificationEntityType.Airline),
            ProviderJobs.VerificationCountry =>
                _verificationOptions.DefaultSourcesFor(VerificationEntityType.Country),
            _ => throw new InvalidOperationException($"'{job}' is not a known provider routing key.")
        };

        public bool IsCeilinged(string job) => job switch
        {
            ProviderJobs.FlightSearchRoute => !_aeroDataBox.Value.IsConfigured,
            ProviderJobs.VerificationAirport or ProviderJobs.VerificationAirline
                or ProviderJobs.VerificationCountry => false,
            _ => throw new InvalidOperationException($"'{job}' is not a known provider routing key.")
        };
    }
}
