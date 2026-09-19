using FlightAssistant.Application.Interfaces.Providers;
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

        public GetProviderRoutesQueryHandler(IProviderRouting routing, IProviderRouteDefaults defaults)
        {
            _routing = routing;
            _defaults = defaults;
        }

        public async Task<IReadOnlyList<AdminProviderRouteDto>> Handle(
            GetProviderRoutesQuery request, CancellationToken ct)
        {
            var rows = new List<AdminProviderRouteDto>(ProviderJobs.All.Count);

            foreach (var job in ProviderJobs.All)
            {
                var state = await _routing.GetAsync(job, ct);
                rows.Add(new AdminProviderRouteDto(
                    Job: job,
                    DisplayName: ProviderJobs.DisplayNameFor(job),
                    Providers: state.Providers,
                    Options: ProviderJobs.OptionsFor(job),
                    IsDefault: state.IsDefault,
                    CanChange: !_defaults.IsCeilinged(job),
                    FixedReason: null,
                    ChangedUtc: state.ChangedUtc,
                    ChangedBy: state.ChangedBy,
                    Reason: state.Reason));
            }

            return rows;
        }
    }
}
