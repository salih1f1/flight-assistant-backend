using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Loyalty
{
    public record MembershipResult(
        Guid Id,
        int ProgramId,
        string AccountNumber,
        int? TierId,
        DateOnly? TierExpiryDate,
        DateOnly? PointsExpiryDate
    );

    public record GetTravelerMembershipsQuery(Guid TravelerId) : IRequest<IReadOnlyList<MembershipResult>>;

    public sealed class GetTravelerMembershipsQueryHandler : IRequestHandler<GetTravelerMembershipsQuery, IReadOnlyList<MembershipResult>>
    {
        private readonly ILoyaltyRepository _loyalty;

        public GetTravelerMembershipsQueryHandler(ILoyaltyRepository loyalty) => _loyalty = loyalty;

        public async Task<IReadOnlyList<MembershipResult>> Handle(GetTravelerMembershipsQuery query, CancellationToken ct)
        {
            var memberships = await _loyalty.GetMembershipsForTravelerAsync(query.TravelerId, ct);
            return memberships.Select(m => new MembershipResult(
                m.Id, m.ProgramId, m.AccountNumber, m.TierId, m.TierExpiryDate, m.PointsExpiryDate
            )).ToList();
        }
    }
}
