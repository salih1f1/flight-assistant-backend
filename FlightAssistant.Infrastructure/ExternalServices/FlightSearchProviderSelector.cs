using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Providers;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices
{
    /// <summary>
    /// Routes a search by criteria shape (#422): a full route goes to AeroDataBox, whose free tier
    /// makes it cost nothing; anything anchored on a flight number stays on AeroAPI, because
    /// AeroDataBox resolves a marketing number to its operator (6E4120 -> TK 1028) and would change
    /// which flight a traveler tracks. Falls back to AeroAPI when AeroDataBox is unconfigured.
    ///
    /// <para>
    /// The decision itself now lives in <see cref="IFlightSearchRouter"/>, not here (#531): this
    /// selector only dispatches to whichever provider name the router resolves — configuration, the
    /// criteria-shape rule above, and the admin routing switch are all the router's concern. It
    /// moved out so <c>AeroApiFlightSearchCachingDecorator</c> can read the identical decision for
    /// its cache key without re-deriving it, the same reason the router calls
    /// <c>AeroApiFlightSearchProvider.HasUsableFlightNumber</c> directly rather than restating its
    /// rule.
    /// </para>
    /// </summary>
    public sealed class FlightSearchProviderSelector : IFlightSearchProvider
    {
        private readonly IFlightSearchProvider _aeroDataBox;
        private readonly IFlightSearchProvider _aeroApi;
        private readonly IFlightSearchRouter _router;
        private readonly ILogger<FlightSearchProviderSelector> _logger;

        public FlightSearchProviderSelector(
            IFlightSearchProvider aeroDataBox,
            IFlightSearchProvider aeroApi,
            IFlightSearchRouter router,
            ILogger<FlightSearchProviderSelector> logger)
        {
            _aeroDataBox = aeroDataBox;
            _aeroApi = aeroApi;
            _router = router;
            _logger = logger;
        }

        public async Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default)
        {
            var provider = await _router.ResolveAsync(criteria, ct);

            // Logged for both providers: since #531 a route search can land on AeroAPI through the
            // admin switch or a missing key, and that is the case worth seeing.
            _logger.LogDebug(
                "Routing flight search {Departure}-{Arrival} {FlightNumber} on {Date} to {Provider}",
                criteria.DepartureIata, criteria.ArrivalIata, criteria.FlightNumber, criteria.Date, provider);

            return string.Equals(provider, ProviderJobs.AeroDataBox, StringComparison.Ordinal)
                ? await _aeroDataBox.SearchAsync(criteria, ct)
                : await _aeroApi.SearchAsync(criteria, ct);
        }
    }
}
