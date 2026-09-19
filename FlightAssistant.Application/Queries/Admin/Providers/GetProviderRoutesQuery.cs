using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Providers;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Providers
{
    /// <summary>Every routing decision, its current value, and whether it can be changed. Backs
    /// <c>GET /admin/providers</c>. PR 3 widens this to the full operation map.</summary>
    public sealed record GetProviderRoutesQuery : IRequest<IReadOnlyList<AdminProviderRouteDto>>;

    public sealed class GetProviderRoutesQueryHandler
        : IRequestHandler<GetProviderRoutesQuery, IReadOnlyList<AdminProviderRouteDto>>
    {
        private readonly IProviderRouting _routing;
        private readonly IProviderRouteDefaults _defaults;
        private readonly IVerificationSourceRegistry _registry;

        public GetProviderRoutesQueryHandler(
            IProviderRouting routing, IProviderRouteDefaults defaults, IVerificationSourceRegistry registry)
        {
            _routing = routing;
            _defaults = defaults;
            _registry = registry;
        }

        public async Task<IReadOnlyList<AdminProviderRouteDto>> Handle(
            GetProviderRoutesQuery request, CancellationToken ct)
        {
            var rows = new List<AdminProviderRouteDto>(ProviderJobs.All.Count);

            foreach (var job in ProviderJobs.All)
            {
                var state = await _routing.GetAsync(job, ct);
                var ceilinged = _defaults.IsCeilinged(job);

                // #531 fix round 1, Finding 2: when configuration ceilings this key, report the
                // effective providers (the configured default), not the stored row's. On the
                // documented rollback path -- a row saying e.g. "aerodatabox" exists, then
                // AeroDataBox:ApiKey is pulled -- the stored row and reality diverge: every real
                // search now goes wherever the ceiling forces it, while the stored row still says
                // what an operator last chose before the environment changed under it. Reporting
                // the stored value here would have this endpoint say "aerodatabox, cannot change"
                // while every search actually used AeroAPI -- the same drift AdminJobDto.EffectiveEnabled
                // exists to prevent for job switches. The stored row itself is unaffected by this
                // and remains visible in the data browser (BrowsableTables' "provider-routes"
                // entry) for an operator who needs the raw history.
                var providers = ceilinged ? _defaults.For(job) : state.Providers;

                // Task 9: a verification key has no static option list (ProviderJobs.OptionsFor
                // returns an empty array for the three of them) -- the real answer is every
                // registered source whose SupportedEntityTypes includes that key's entity type,
                // which only IVerificationSourceRegistry knows. ProviderJobs.SupportedSourceNames
                // is the one place that expression lives, so this can never advertise an option
                // SetProviderRouteCommandHandler's own validation would then reject.
                var options = job == ProviderJobs.FlightSearchRoute
                    ? ProviderJobs.OptionsFor(job)
                    : ProviderJobs.SupportedSourceNames(_registry, ProviderJobs.EntityTypeFor(job));

                rows.Add(new AdminProviderRouteDto(
                    Job: job,
                    DisplayName: ProviderJobs.DisplayNameFor(job),
                    Providers: providers,
                    Options: options,
                    IsDefault: state.IsDefault,
                    CanChange: !ceilinged,
                    FixedReason: null,
                    ChangedUtc: state.ChangedUtc,
                    ChangedBy: state.ChangedBy,
                    Reason: state.Reason));
            }

            return rows;
        }
    }
}
