using FlightAssistant.Domain.Providers.Entities;

namespace FlightAssistant.Application.Interfaces.Providers
{
    /// <summary>
    /// Write side for <c>provider_routes</c>. Kept apart from <see cref="IProviderRouting"/>'s
    /// cached read side so a write can never inherit that policy: a write must always reach the
    /// database, never be served from a cache entry, and never silently "succeed" against a
    /// database it did not reach. Same split, same reason, as <c>IJobSwitchRepository</c>.
    /// </summary>
    public interface IProviderRouteRepository
    {
        /// <summary>Inserts or overwrites the row for <c>row.Job</c>.</summary>
        Task UpsertAsync(ProviderRoute row, CancellationToken ct);
    }
}
