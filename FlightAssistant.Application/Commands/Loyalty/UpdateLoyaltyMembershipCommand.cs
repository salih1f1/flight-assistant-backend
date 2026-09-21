using FlightAssistant.Application.Commands.Admin.Common;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using MediatR;

namespace FlightAssistant.Application.Commands.Loyalty
{
    public record UpdateLoyaltyMembershipCommand(
        Guid TravelerId,
        Guid MembershipId,
        string? AccountNumber,
        int? TierId,
        DateOnly? TierExpiryDate
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

            // Add has checked the tier against the program since #475; this path never did, so a
            // tier of another program was saved as-is (#548). The program is the membership's own —
            // it cannot change here.
            await ReferenceExistence.EnsureLoyaltyProgramAndTierExistAsync(_loyalty, membership.ProgramId, cmd.TierId, ct);

            // An omitted account number keeps the one held, so a tier-only change needs no resend (#548).
            membership.Update(cmd.AccountNumber ?? membership.AccountNumber, cmd.TierId, cmd.TierExpiryDate);
            await _loyalty.SaveChangesAsync(ct);
        }
    }
}
