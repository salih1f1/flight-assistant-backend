using FlightAssistant.Application.Commands.Admin.Common;
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
        DateOnly? TierExpiryDate
    ) : IRequest<Guid>;

    public sealed class AddLoyaltyMembershipCommandHandler : IRequestHandler<AddLoyaltyMembershipCommand, Guid>
    {
        private readonly ILoyaltyRepository _loyalty;

        public AddLoyaltyMembershipCommandHandler(ILoyaltyRepository loyalty) => _loyalty = loyalty;

        public async Task<Guid> Handle(AddLoyaltyMembershipCommand cmd, CancellationToken ct)
        {
            // The validator checks shape only, so without this an unknown program — or a tier from
            // another program — reached Postgres and failed its foreign key as a 500 (#475).
            await ReferenceExistence.EnsureLoyaltyProgramAndTierExistAsync(_loyalty, cmd.ProgramId, cmd.TierId, ct);

            var membership = TravelerLoyaltyMembership.Create(
                cmd.TravelerId, cmd.ProgramId, cmd.AccountNumber,
                cmd.TierId, cmd.TierExpiryDate);

            await _loyalty.AddMembershipAsync(membership, ct);
            await _loyalty.SaveChangesAsync(ct);
            return membership.Id;
        }
    }
}
