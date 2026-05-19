using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
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
                ?? throw new DomainException($"Membership {cmd.MembershipId} not found.", 404);

            if (membership.TravelerId != cmd.TravelerId)
                throw new DomainException("Cannot update another traveler's membership.", 403);

            membership.Update(cmd.AccountNumber, cmd.TierId, cmd.TierExpiryDate, cmd.PointsExpiryDate);
            await _loyalty.SaveChangesAsync(ct);
        }
    }
}
