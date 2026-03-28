using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Commands.Loyalty
{
    public record UpdateLoyaltyMembershipCommand(
        Guid TravelerId,
        Guid MembershipId,
        string AccountNumber,
        int? TierId,
        DateOnly? TierExpiryDate,
        DateOnly? PointsExpiryDate
    ) : IRequest;

    public sealed class UpdateLoyaltyMembershipCommandHandler : IRequestHandler<UpdateLoyaltyMembershipCommand>
    {
        private readonly ILoyaltyRepository _loyalty;

        public UpdateLoyaltyMembershipCommandHandler(ILoyaltyRepository loyalty) => _loyalty = loyalty;

        public async Task Handle(UpdateLoyaltyMembershipCommand cmd, CancellationToken ct)
        {
            var membership = await _loyalty.GetMembershipByIdAsync(cmd.MembershipId, ct)
                ?? throw new InvalidOperationException($"Membership {cmd.MembershipId} not found.");

            if (membership.TravelerId != cmd.TravelerId)
                throw new UnauthorizedAccessException("Cannot update another traveler's membership.");

            membership.Update(cmd.AccountNumber, cmd.TierId, cmd.TierExpiryDate, cmd.PointsExpiryDate);
            await _loyalty.SaveChangesAsync(ct);
        }
    }
}
