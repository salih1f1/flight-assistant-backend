using FlightAssistant.Application.Interfaces.ExternalServices;

namespace FlightAssistant.Infrastructure.ExternalServices
{
    /// <summary>
    /// Which provider a given search goes to. Extracted from <see cref="FlightSearchProviderSelector"/>
    /// (#531) so there is exactly one copy of the rule and two readers of it: the selector, which
    /// routes the call, and <c>AeroApiFlightSearchCachingDecorator</c>, which puts the answer in its
    /// cache key.
    ///
    /// <para>
    /// The decorator must not re-derive this. A second copy would be free to drift, which is the
    /// same reason the selector <i>calls</i>
    /// <c>AeroApiFlightSearchProvider.HasUsableFlightNumber</c> rather than restating its rule.
    /// </para>
    /// </summary>
    public interface IFlightSearchRouter
    {
        /// <summary>Returns <c>ProviderJobs.AeroDataBox</c> or <c>ProviderJobs.AeroApi</c>.</summary>
        Task<string> ResolveAsync(FlightSearchCriteria criteria, CancellationToken ct);
    }
}
