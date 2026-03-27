using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Commands.Loyalty
{
    public record RemoveLoyaltyMembershipCommand(Guid TravelerId, Guid MembershipId) : IRequest;

    public sealed class RemoveLoyaltyMembershipCommandHandler : IRequestHandler<RemoveLoyaltyMembershipCommand>
    {
        private readonly ILoyaltyRepository _loyalty;

        public RemoveLoyaltyMembershipCommandHandler(ILoyaltyRepository loyalty) => _loyalty = loyalty;

        public async Task Handle(RemoveLoyaltyMembershipCommand cmd, CancellationToken ct)
        {
            var membership = await _loyalty.GetMembershipByIdAsync(cmd.MembershipId, ct);
            if (membership is null) return; // already removed — idempotent

            if (membership.TravelerId != cmd.TravelerId)
                throw new UnauthorizedAccessException("Cannot remove another traveler's membership.");

            await _loyalty.RemoveMembershipAsync(membership, ct);
            await _loyalty.SaveChangesAsync(ct);
        }
    }
}
