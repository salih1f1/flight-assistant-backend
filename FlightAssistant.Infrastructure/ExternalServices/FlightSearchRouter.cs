using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Providers;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.ExternalServices
{
    /// <inheritdoc cref="IFlightSearchRouter"/>
    public sealed class FlightSearchRouter : IFlightSearchRouter
    {
        private readonly IProviderRouting _routing;
        private readonly IOptions<AeroDataBoxOptions> _aeroDataBoxOptions;
        private readonly ILogger<FlightSearchRouter> _logger;

        public FlightSearchRouter(
            IProviderRouting routing,
            IOptions<AeroDataBoxOptions> aeroDataBoxOptions,
            ILogger<FlightSearchRouter> logger)
        {
            _routing = routing;
            _aeroDataBoxOptions = aeroDataBoxOptions;
            _logger = logger;
        }

        public async Task<string> ResolveAsync(FlightSearchCriteria criteria, CancellationToken ct)
        {
            // The criteria-shape rule is unchanged from #422 and is NOT switchable. A flight-number
            // search stays on AeroAPI for correctness, and every shape AeroAPI's own search guard
            // rejects still reaches that guard so it keeps producing the same error for the same
            // input it always has.
            if (!IsRouteShaped(criteria))
                return ProviderJobs.AeroApi;

            // Configuration is a hard ceiling, exactly as it is for job switches.
            if (!_aeroDataBoxOptions.Value.IsConfigured)
                return ProviderJobs.AeroApi;

            var providers = await _routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, ct);
            var chosen = providers.Count > 0 ? providers[0] : ProviderJobs.AeroDataBox;

            if (string.Equals(chosen, ProviderJobs.AeroDataBox, StringComparison.Ordinal))
                return ProviderJobs.AeroDataBox;

            if (string.Equals(chosen, ProviderJobs.AeroApi, StringComparison.Ordinal))
                return ProviderJobs.AeroApi;

            // Only reachable if a row was written outside SetProviderRouteCommand, whose validator
            // rejects anything not in ProviderJobs.OptionsFor. Fall back rather than throw: a bad
            // row must not take out every search.
            _logger.LogWarning(
                "Provider route {Job} names unknown provider '{Provider}'; using AeroDataBox.",
                ProviderJobs.FlightSearchRoute, chosen);
            return ProviderJobs.AeroDataBox;
        }

        private static bool IsRouteShaped(FlightSearchCriteria criteria) =>
            !AeroApiFlightSearchProvider.HasUsableFlightNumber(criteria.FlightNumber) &&
            !string.IsNullOrWhiteSpace(criteria.DepartureIata) &&
            !string.IsNullOrWhiteSpace(criteria.ArrivalIata);
    }
}
