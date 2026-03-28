using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Loyalty
{
    public record AddLoyaltyMembershipCommand(
        Guid TravelerId,
        int ProgramId,
        string AccountNumber,
        int? TierId,
        DateOnly? TierExpiryDate,
        DateOnly? PointsExpiryDate
    ) : IRequest<Guid>;

    public sealed class AddLoyaltyMembershipCommandHandler : IRequestHandler<AddLoyaltyMembershipCommand, Guid>
    {
        private readonly ILoyaltyRepository _loyalty;

        public AddLoyaltyMembershipCommandHandler(ILoyaltyRepository loyalty) => _loyalty = loyalty;

        public async Task<Guid> Handle(AddLoyaltyMembershipCommand cmd, CancellationToken ct)
        {
            var membership = TravelerLoyaltyMembership.Create(
                cmd.TravelerId, cmd.ProgramId, cmd.AccountNumber,
                cmd.TierId, cmd.TierExpiryDate, cmd.PointsExpiryDate);

            await _loyalty.AddMembershipAsync(membership, ct);
            await _loyalty.SaveChangesAsync(ct);
            return membership.Id;
        }
    }
}
