using FlightAssistant.Application.Interfaces.Repositories;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Loyalty
{
    public record DeleteLoyaltyTierCommand(int ProgramId, int TierId) : IRequest;

    public sealed class DeleteLoyaltyTierCommandHandler : IRequestHandler<DeleteLoyaltyTierCommand>
    {
        private readonly ILoyaltyRepository _repo;
        public DeleteLoyaltyTierCommandHandler(ILoyaltyRepository repo) => _repo = repo;

        public async Task Handle(DeleteLoyaltyTierCommand cmd, CancellationToken ct)
        {
            var program = LoyaltyTierGuard.EnsureProgram(await _repo.GetProgramByIdAsync(cmd.ProgramId, ct), cmd.ProgramId);
            // Unlike deleting a program, which returns silently, an unknown tier is a 404: the tier
            // is addressed through its program, so an id that isn't on it is a mistake, not a no-op.
            var tier = LoyaltyTierGuard.EnsureTier(program, cmd.TierId);

            program.RemoveTier(tier.Id);
            await _repo.SaveChangesAsync(ct);
        }
    }

    public sealed class DeleteLoyaltyTierCommandValidator : AbstractValidator<DeleteLoyaltyTierCommand>
    {
        public DeleteLoyaltyTierCommandValidator()
        {
            RuleFor(x => x.ProgramId).GreaterThan(0);
            RuleFor(x => x.TierId).GreaterThan(0);
        }
    }
}
