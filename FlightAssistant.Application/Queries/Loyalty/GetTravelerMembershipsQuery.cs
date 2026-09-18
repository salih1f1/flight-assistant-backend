using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Validators;
using FlightAssistant.Contracts.Paging;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Loyalty
{
    public record MembershipResult(
        Guid Id,
        int ProgramId,
        string AccountNumber,
        int? TierId,
        DateOnly? TierExpiryDate
    );

    public record GetTravelerMembershipsQuery(Guid TravelerId, PagedRequest Paging) : IRequest<PagedResult<MembershipResult>>;

    public class GetTravelerMembershipsQueryValidator : AbstractValidator<GetTravelerMembershipsQuery>
    {
        public GetTravelerMembershipsQueryValidator()
        {
            RuleFor(x => x.Paging).SetValidator(new PagedRequestValidator());
        }
    }

    public sealed class GetTravelerMembershipsQueryHandler : IRequestHandler<GetTravelerMembershipsQuery, PagedResult<MembershipResult>>
    {
        private readonly ILoyaltyRepository _loyalty;

        public GetTravelerMembershipsQueryHandler(ILoyaltyRepository loyalty) => _loyalty = loyalty;

        public async Task<PagedResult<MembershipResult>> Handle(GetTravelerMembershipsQuery query, CancellationToken ct)
        {
            var (memberships, total) = await _loyalty.GetMembershipsForTravelerAsync(query.TravelerId, query.Paging.Page, query.Paging.PageSize, ct);
            var items = memberships.Select(m => new MembershipResult(
                m.Id, m.ProgramId, m.AccountNumber, m.TierId, m.TierExpiryDate
            )).ToList();
            return new PagedResult<MembershipResult>(items, total, query.Paging.Page, query.Paging.PageSize);
        }
    }
}
