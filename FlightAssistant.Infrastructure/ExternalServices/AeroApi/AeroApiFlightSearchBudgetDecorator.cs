using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Refuses a search that would reach AeroAPI once this month's spend passes the budget guard's
    /// soft limit (#303), with a 503 that names the budget. It sits inside
    /// <see cref="Caching.AeroApiFlightSearchCachingDecorator"/>, so a cached search is still answered
    /// and only a cache miss is refused. The provider makes its timezone lookups, so a refused search
    /// makes none of those either.
    /// </summary>
    public sealed class AeroApiFlightSearchBudgetDecorator : IFlightSearchProvider
    {
        private const int StatusCode503ServiceUnavailable = 503;

        private readonly IFlightSearchProvider _inner;
        private readonly IProviderBudgetGuard _budget;
        private readonly ILogger<AeroApiFlightSearchBudgetDecorator> _logger;

        public AeroApiFlightSearchBudgetDecorator(
            IFlightSearchProvider inner,
            IProviderBudgetGuard budget,
            ILogger<AeroApiFlightSearchBudgetDecorator> logger)
        {
            _inner = inner;
            _budget = budget;
            _logger = logger;
        }

        public async Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default)
        {
            var state = await _budget.GetStateAsync(ct);
            if (state == ProviderBudgetState.Normal)
                return await _inner.SearchAsync(criteria, ct);

            _logger.LogInformation(
                "Refused an uncached flight search {Origin}-{Destination} {FlightNumber} on {Date}: AeroAPI budget is {State}",
                criteria.DepartureIata, criteria.ArrivalIata, criteria.FlightNumber, criteria.Date, state);

            throw new DomainException(
                state == ProviderBudgetState.Exhausted
                    ? "Flight search is paused: this month's flight data budget is used up. Please try again later."
                    : "Flight search is paused: this month's flight data budget is nearly used up. Please try again later.",
                StatusCode503ServiceUnavailable,
                $"AeroAPI budget state is {state}: searches that aren't cached are refused from the soft limit (#303).");
        }
    }
}
