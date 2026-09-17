using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.ExternalServices
{
    /// <summary>
    /// Routes a search by criteria shape (#422): a full route goes to AeroDataBox, whose free tier
    /// makes it cost nothing; anything anchored on a flight number stays on AeroAPI, because
    /// AeroDataBox resolves a marketing number to its operator (6E4120 -> TK 1028) and would change
    /// which flight a traveler tracks. Falls back to AeroAPI when AeroDataBox is unconfigured.
    ///
    /// <para>
    /// Routes to AeroDataBox only when <b>all three</b> hold: AeroDataBox is configured
    /// (<see cref="AeroDataBoxOptions.IsConfigured"/>), the criteria carries no usable flight
    /// number (<see cref="AeroApiFlightSearchProvider.HasUsableFlightNumber"/> — called, never
    /// restated, so the two guards cannot drift), and both
    /// <see cref="FlightSearchCriteria.DepartureIata"/> and
    /// <see cref="FlightSearchCriteria.ArrivalIata"/> are non-whitespace. Every other shape —
    /// including every shape AeroAPI's own search guard rejects (a single airport, both airports
    /// blank, a too-short flight number with no route) — goes to AeroAPI, so that guard keeps
    /// producing the same error for the same input it always has.
    /// </para>
    /// </summary>
    public sealed class FlightSearchProviderSelector : IFlightSearchProvider
    {
        private readonly IFlightSearchProvider _aeroDataBox;
        private readonly IFlightSearchProvider _aeroApi;
        private readonly IOptions<AeroDataBoxOptions> _aeroDataBoxOptions;
        private readonly ILogger<FlightSearchProviderSelector> _logger;

        public FlightSearchProviderSelector(
            IFlightSearchProvider aeroDataBox,
            IFlightSearchProvider aeroApi,
            IOptions<AeroDataBoxOptions> aeroDataBoxOptions,
            ILogger<FlightSearchProviderSelector> logger)
        {
            _aeroDataBox = aeroDataBox;
            _aeroApi = aeroApi;
            _aeroDataBoxOptions = aeroDataBoxOptions;
            _logger = logger;
        }

        public Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default)
        {
            if (IsRouteSearchForAeroDataBox(criteria))
            {
                _logger.LogDebug(
                    "Routing flight search {Departure}-{Arrival} on {Date} to AeroDataBox (#422)",
                    criteria.DepartureIata, criteria.ArrivalIata, criteria.Date);
                return _aeroDataBox.SearchAsync(criteria, ct);
            }

            return _aeroApi.SearchAsync(criteria, ct);
        }

        private bool IsRouteSearchForAeroDataBox(FlightSearchCriteria criteria) =>
            _aeroDataBoxOptions.Value.IsConfigured &&
            !AeroApiFlightSearchProvider.HasUsableFlightNumber(criteria.FlightNumber) &&
            !string.IsNullOrWhiteSpace(criteria.DepartureIata) &&
            !string.IsNullOrWhiteSpace(criteria.ArrivalIata);
    }
}
