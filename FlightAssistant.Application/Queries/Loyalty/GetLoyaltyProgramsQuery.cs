using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Loyalty
{
    public record LoyaltyTierResult(int Id, string Code, string Name, int Rank, string? AllianceCode, string? AllianceTierCode);

    public record LoyaltyProgramResult(int Id, string Code, string Name, string Kind, int? AirlineId, string? AllianceCode, IReadOnlyList<LoyaltyTierResult> Tiers);

    public record GetLoyaltyProgramsQuery : IRequest<IReadOnlyList<LoyaltyProgramResult>>;

    public sealed class GetLoyaltyProgramsQueryHandler : IRequestHandler<GetLoyaltyProgramsQuery, IReadOnlyList<LoyaltyProgramResult>>
    {
        private readonly ILoyaltyRepository _loyalty;

        public GetLoyaltyProgramsQueryHandler(ILoyaltyRepository loyalty) => _loyalty = loyalty;

        public async Task<IReadOnlyList<LoyaltyProgramResult>> Handle(GetLoyaltyProgramsQuery query, CancellationToken ct)
        {
            var programs = await _loyalty.GetProgramsAsync(ct);
            return programs.Select(p => new LoyaltyProgramResult(
                p.Id, p.Code, p.Name, p.Kind.Code, p.AirlineId, p.AllianceCode,
                p.Tiers.Select(t => new LoyaltyTierResult(t.Id, t.Code, t.Name, t.Rank, t.AllianceCode, t.AllianceTierCode)).ToList()
            )).ToList();
        }
    }
}
