using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Validators;
using FlightAssistant.Contracts.Paging;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Loyalty
{
    public record LoyaltyTierResult(int Id, string Code, string Name, int Rank, string? AllianceCode, string? AllianceTierCode);

    public record LoyaltyProgramResult(int Id, string Code, string Name, string Kind, int? AirlineId, IReadOnlyList<LoyaltyTierResult> Tiers);

    public record GetLoyaltyProgramsQuery(PagedRequest Paging) : IRequest<PagedResult<LoyaltyProgramResult>>;

    public class GetLoyaltyProgramsQueryValidator : AbstractValidator<GetLoyaltyProgramsQuery>
    {
        public GetLoyaltyProgramsQueryValidator()
        {
            RuleFor(x => x.Paging).SetValidator(new PagedRequestValidator());
        }
    }

    public sealed class GetLoyaltyProgramsQueryHandler : IRequestHandler<GetLoyaltyProgramsQuery, PagedResult<LoyaltyProgramResult>>
    {
        private readonly ILoyaltyRepository _loyalty;

        public GetLoyaltyProgramsQueryHandler(ILoyaltyRepository loyalty) => _loyalty = loyalty;

        public async Task<PagedResult<LoyaltyProgramResult>> Handle(GetLoyaltyProgramsQuery query, CancellationToken ct)
        {
            var (programs, total) = await _loyalty.GetProgramsAsync(query.Paging.Page, query.Paging.PageSize, ct);
            var items = programs.Select(p => new LoyaltyProgramResult(
                p.Id, p.Code, p.Name, p.Kind.Code, p.AirlineId,
                p.Tiers.Select(t => new LoyaltyTierResult(t.Id, t.Code, t.Name, t.Rank, t.AllianceCode, t.AllianceTierCode)).ToList()
            )).ToList();
            return new PagedResult<LoyaltyProgramResult>(items, total, query.Paging.Page, query.Paging.PageSize);
        }
    }
}
