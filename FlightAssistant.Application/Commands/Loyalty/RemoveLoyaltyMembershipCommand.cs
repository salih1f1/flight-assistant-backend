using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
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
            var membership = await _loyalty.GetMembershipByIdAsync(cmd.MembershipId, ct);
            if (membership is null) return; // already removed — idempotent

            if (membership.TravelerId != cmd.TravelerId)
                throw new DomainException("Cannot remove another traveler's membership.", 403);

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
