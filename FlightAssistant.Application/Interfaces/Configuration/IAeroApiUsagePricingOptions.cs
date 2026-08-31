namespace FlightAssistant.Application.Interfaces.Configuration
{
    /// <summary>
    /// The pricing and budget figures <c>GetAeroApiUsageQueryHandler</c> needs to turn grouped call
    /// counts into an estimated USD spend. Defined here (Application) rather than depended on
    /// directly, because the real values live on <c>AeroApiOptions</c> in Infrastructure and
    /// Application must never reference Infrastructure — <c>AeroApiOptions</c> implements this
    /// interface, the same pattern used for
    /// <see cref="FlightAssistant.Application.Interfaces.ExternalServices.IFlightSearchHorizonOptions"/>.
    /// See the DI registration in <c>FlightAssistant.Infrastructure.DependencyInjection</c>.
    /// </summary>
    public interface IAeroApiUsagePricingOptions
    {
        /// <summary>
        /// Unit cost in USD per <c>EndpointTemplate</c>. A template with no entry here prices at 0
        /// rather than throwing — a missing price should show up as a visible $0 line in the usage
        /// rollup, not fail it.
        /// </summary>
        IReadOnlyDictionary<string, decimal> Pricing { get; }

        /// <summary>Informational budget ceiling shown against actual metered spend; enforces nothing on its own.</summary>
        decimal MonthlyBudgetUsd { get; }
    }
}
