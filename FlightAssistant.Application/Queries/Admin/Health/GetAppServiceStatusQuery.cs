using FlightAssistant.Application.Hosting;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Health
{
    /// <summary>
    /// Reads the App Service hosting this API (#530). No parameters: there is exactly one App
    /// Service to ask about -- the one running this process.
    /// </summary>
    public sealed record GetAppServiceStatusQuery : IRequest<AppServiceStatusDto>;

    /// <summary>
    /// The one copy of how the App Service snapshot is cached -- the same shape
    /// <c>AzureBudgetCache</c> (in <c>GetAzureBudgetQuery.cs</c>) uses, narrowed further: only
    /// <see cref="AppServiceState.Ok"/> is ever written. <see cref="AppServiceState.NotAuthorized"/>
    /// and <see cref="AppServiceState.Unavailable"/> are refusals an operator can act on -- grant
    /// the Reader role, wait out an outage -- and caching either for the five-minute window would
    /// leave a stale wrong answer on screen for exactly the moment it is most annoying: right after
    /// the grant was fixed. Unlike the budget query, <see cref="AppServiceState.NotConfigured"/> is
    /// not cached alongside Ok either: it is not a refusal, but it never occurs anywhere except a
    /// developer machine that will not still be running five minutes from now in a way anyone is
    /// watching, so there is nothing to gain by writing it.
    /// </summary>
    internal static class AppServiceStatusCache
    {
        /// <summary>Always On is a platform setting an operator flips and then immediately wants
        /// reflected -- five minutes is short enough that the panel shows the change on its next
        /// load without a hard refresh, long enough that the panel being open does not itself turn
        /// into a stream of ARM calls.</summary>
        public static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);

        private const string Key = "hosting:app-service";

        public static async Task<AppServiceSnapshot> GetOrFetchAsync(
            IAppServiceProvider provider, ICacheService cache, CancellationToken ct)
        {
            var cached = await cache.GetAsync<AppServiceSnapshot>(Key, ct);
            if (cached is not null)
                return cached;

            var snapshot = await provider.GetStatusAsync(ct);
            if (snapshot.State is AppServiceState.Ok)
                await cache.SetAsync(Key, snapshot, Duration, ct);

            return snapshot;
        }
    }

    /// <summary>
    /// Maps the provider's <see cref="AppServiceSnapshot"/> straight to the wire DTO -- there is no
    /// intermediate Result type, the same as this handler's closest model, <c>GetAzureBudgetQueryHandler</c>,
    /// because nothing here needs deriving beyond stringifying the state: every other field is
    /// already exactly what the panel binds to.
    /// </summary>
    public sealed class GetAppServiceStatusQueryHandler : IRequestHandler<GetAppServiceStatusQuery, AppServiceStatusDto>
    {
        private readonly IAppServiceProvider _provider;
        private readonly ICacheService _cache;

        public GetAppServiceStatusQueryHandler(IAppServiceProvider provider, ICacheService cache)
        {
            _provider = provider;
            _cache = cache;
        }

        public async Task<AppServiceStatusDto> Handle(GetAppServiceStatusQuery request, CancellationToken ct)
        {
            var snapshot = await AppServiceStatusCache.GetOrFetchAsync(_provider, _cache, ct);

            return new AppServiceStatusDto(
                snapshot.State.ToString(),
                snapshot.SiteName,
                snapshot.SiteState,
                snapshot.AvailabilityState,
                snapshot.Enabled,
                snapshot.AlwaysOn,
                snapshot.InstanceCount,
                snapshot.Message);
        }
    }
}
