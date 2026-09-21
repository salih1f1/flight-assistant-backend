using FlightAssistant.Application.Interfaces.Repositories;
using FluentValidation;
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
            // Another traveler's membership is treated as missing, so the response never reveals
            // that the id exists (#550).
            var membership = await _loyalty.GetMembershipByIdAsync(cmd.MembershipId, ct);
            if (membership is null || membership.TravelerId != cmd.TravelerId)
                return; // already removed — idempotent

            await _loyalty.RemoveMembershipAsync(membership, ct);
            await _loyalty.SaveChangesAsync(ct);
        }
    }

    public sealed class RemoveLoyaltyMembershipCommandValidator : AbstractValidator<RemoveLoyaltyMembershipCommand>
    {
        public RemoveLoyaltyMembershipCommandValidator()
        {
            RuleFor(x => x.MembershipId).NotEqual(Guid.Empty);
        }
    }
}
