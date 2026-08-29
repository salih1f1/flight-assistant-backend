using FlightAssistant.Application.Interfaces.ExternalServices;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.FlightSearch
{
    /// <summary>
    /// Sends a search to whichever AviationStack endpoint actually holds data for the requested
    /// date, hiding the split from the rest of the application.
    ///
    /// AviationStack serves past and present flights from <c>/v1/flights</c> and future schedules
    /// from <c>/v1/flightsFuture</c>, with no overlap: a future date on the real-time endpoint
    /// returns an empty set rather than an error, which is indistinguishable from "no such route".
    /// Routing here keeps that quirk out of the query handler and the public contract.
    ///
    /// The boundary is UTC today. A late-evening departure whose local date differs from the UTC
    /// date may be served by either provider; both return correct data for it, so the ambiguity is
    /// harmless.
    /// </summary>
    public sealed class DateRoutingFlightSearchProvider : IFlightSearchProvider
    {
        private readonly AviationStackLiveSearchProvider _live;
        private readonly AviationStackScheduleSearchProvider _schedule;
        private readonly ILogger<DateRoutingFlightSearchProvider> _logger;
        private readonly TimeProvider _clock;

        public DateRoutingFlightSearchProvider(
            AviationStackLiveSearchProvider live,
            AviationStackScheduleSearchProvider schedule,
            ILogger<DateRoutingFlightSearchProvider> logger,
            TimeProvider? clock = null)
        {
            _live = live;
            _schedule = schedule;
            _logger = logger;
            _clock = clock ?? TimeProvider.System;
        }

        public Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default)
        {
            var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
            var isFuture = criteria.Date > today;

            _logger.LogDebug(
                "Routing flight search for {Date} to the {Endpoint} provider (UTC today is {Today})",
                criteria.Date, isFuture ? "schedules" : "real-time", today);

            return isFuture
                ? _schedule.SearchAsync(criteria, ct)
                : _live.SearchAsync(criteria, ct);
        }
    }
}
