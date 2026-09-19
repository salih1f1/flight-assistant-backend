using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Usage;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Usage
{
    /// <summary>
    /// Reads the subscription's Cost Management budget and month-to-date spend (#496 task 4). No
    /// parameters: unlike <see cref="GetAeroApiUsageQuery"/> there is no range to pick — Cost
    /// Management reports the current budget period only.
    /// </summary>
    public sealed record GetAzureBudgetQuery : IRequest<AzureBudgetDto>;

    /// <summary>
    /// The one copy of how the budget snapshot is cached. Deliberately narrower than
    /// <see cref="AeroApiUsageCache"/>: only <see cref="AzureBudgetState.Ok"/> and
    /// <see cref="AzureBudgetState.NoBudget"/> are ever written. <see cref="AzureBudgetState.NotAuthorized"/>
    /// and <see cref="AzureBudgetState.Unavailable"/> are refusals an operator can act on — grant
    /// the role, wait out an outage — and caching either for an hour would leave a stale "no" on
    /// screen for exactly the hour it is most annoying: the one right after the role was granted.
    /// </summary>
    internal static class AzureBudgetCache
    {
        /// <summary>Cost Management itself lags 8-24 hours and rate-limits aggressively; an hour
        /// buys real freshness relative to that lag without spending calls on every dashboard load.</summary>
        public static readonly TimeSpan Duration = TimeSpan.FromHours(1);

        private const string Key = "azure:budget";

        public static async Task<AzureBudgetSnapshot> GetOrFetchAsync(
            IAzureCostProvider provider, ICacheService cache, CancellationToken ct)
        {
            var cached = await cache.GetAsync<AzureBudgetSnapshot>(Key, ct);
            if (cached is not null)
                return cached;

            var snapshot = await provider.GetBudgetAsync(ct);
            if (snapshot.State is AzureBudgetState.Ok or AzureBudgetState.NoBudget)
                await cache.SetAsync(Key, snapshot, Duration, ct);

            return snapshot;
        }
    }

    /// <summary>
    /// Maps the provider's <see cref="AzureBudgetSnapshot"/> straight to the wire DTO — there is no
    /// intermediate Result type, unlike <see cref="GetAeroApiUsageQueryHandler"/>, because nothing
    /// here needs deriving beyond stringifying the state: every other field is already exactly what
    /// the panel binds to.
    /// </summary>
    public sealed class GetAzureBudgetQueryHandler : IRequestHandler<GetAzureBudgetQuery, AzureBudgetDto>
    {
        private readonly IAzureCostProvider _provider;
        private readonly ICacheService _cache;

        public GetAzureBudgetQueryHandler(IAzureCostProvider provider, ICacheService cache)
        {
            _provider = provider;
            _cache = cache;
        }

        public async Task<AzureBudgetDto> Handle(GetAzureBudgetQuery request, CancellationToken ct)
        {
            var snapshot = await AzureBudgetCache.GetOrFetchAsync(_provider, _cache, ct);

            return new AzureBudgetDto(
                snapshot.State.ToString(),
                snapshot.BudgetName,
                snapshot.BudgetAmount,
                snapshot.CurrentSpendAmount,
                snapshot.Currency,
                snapshot.PeriodStartUtc,
                snapshot.PeriodEndUtc,
                snapshot.DataAsOfUtc,
                snapshot.Message);
        }
    }
}
