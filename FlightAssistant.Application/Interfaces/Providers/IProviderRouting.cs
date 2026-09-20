using FlightAssistant.Application.Providers;

namespace FlightAssistant.Application.Interfaces.Providers
{
    /// <summary>
    /// The runtime routing value for one decision, read on every search and every verification
    /// enqueue.
    ///
    /// <para>
    /// Answers the switch only, never the configuration ceiling: whether the deployment can reach
    /// the chosen provider at all is a separate question its caller combines in, exactly as
    /// <c>IJobSwitch</c> keeps <c>FlightSync:Enabled</c> out of its own answer.
    /// </para>
    /// </summary>
    public interface IProviderRouting
    {
        /// <summary>
        /// The chosen providers in ask order, cached — a per-request read of a value that changes
        /// maybe twice a month would otherwise be a database round trip per search. Falls back to
        /// the configured default when no row exists, and also when the read fails.
        /// </summary>
        Task<IReadOnlyList<string>> GetProvidersAsync(string job, CancellationToken ct);

        /// <summary>The full row, for the admin endpoint. Uncached — an operator reading the page
        /// after pressing the button must see what they just did, not a stale cache entry.</summary>
        Task<ProviderRouteState> GetAsync(string job, CancellationToken ct);
    }
}
