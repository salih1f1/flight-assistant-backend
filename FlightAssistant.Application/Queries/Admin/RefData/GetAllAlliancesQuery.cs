using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.RefData
{
    public record AllianceResult(string Code, string Name);

    public record GetAllAlliancesQuery : IRequest<IReadOnlyList<AllianceResult>>;

    public sealed class GetAllAlliancesQueryHandler : IRequestHandler<GetAllAlliancesQuery, IReadOnlyList<AllianceResult>>
    {
        private readonly IAllianceRepository _repo;
        public GetAllAlliancesQueryHandler(IAllianceRepository repo) => _repo = repo;

        public async Task<IReadOnlyList<AllianceResult>> Handle(GetAllAlliancesQuery request, CancellationToken ct)
        {
            var alliances = await _repo.GetAllAsync(ct);
            return alliances.Select(a => new AllianceResult(a.Code, a.Name)).ToList();
        }
    }
}
