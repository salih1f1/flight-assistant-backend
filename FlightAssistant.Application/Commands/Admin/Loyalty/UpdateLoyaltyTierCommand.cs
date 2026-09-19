using FlightAssistant.Application.Commands.Admin.Common;
using FlightAssistant.Application.Interfaces.Repositories;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Loyalty
{
    public record UpdateLoyaltyTierCommand(
        int ProgramId,
        int TierId,
        string Code,
        string Name,
        int Rank,
        string? AllianceCode,
        string? AllianceTierCode
    ) : IRequest;

    public sealed class UpdateLoyaltyTierCommandHandler : IRequestHandler<UpdateLoyaltyTierCommand>
    {
        private readonly ILoyaltyRepository _repo;
        private readonly IAllianceRepository _alliances;
        public UpdateLoyaltyTierCommandHandler(ILoyaltyRepository repo, IAllianceRepository alliances)
        {
            _repo = repo;
            _alliances = alliances;
        }

        public async Task Handle(UpdateLoyaltyTierCommand cmd, CancellationToken ct)
        {
            var program = LoyaltyTierGuard.EnsureProgram(await _repo.GetProgramByIdAsync(cmd.ProgramId, ct), cmd.ProgramId);
            var tier = LoyaltyTierGuard.EnsureTier(program, cmd.TierId);
            LoyaltyTierGuard.EnsureNoClash(program, cmd.Code, cmd.Rank, exceptTierId: tier.Id);
            await ReferenceExistence.EnsureAllianceExistsAsync(_alliances, cmd.AllianceCode, "LoyaltyTier", ct);

            tier.Update(cmd.Code, cmd.Name, cmd.Rank, cmd.AllianceCode, cmd.AllianceTierCode);
            await _repo.SaveChangesAsync(ct);
        }
    }

    public sealed class UpdateLoyaltyTierCommandValidator : AbstractValidator<UpdateLoyaltyTierCommand>
    {
        public UpdateLoyaltyTierCommandValidator()
        {
            RuleFor(x => x.ProgramId).GreaterThan(0);
            RuleFor(x => x.TierId).GreaterThan(0);
            RuleFor(x => x.Code).NotEmpty().MaximumLength(24);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Rank).GreaterThanOrEqualTo(0);
            RuleFor(x => x.AllianceCode).MaximumLength(8).When(x => x.AllianceCode is not null);
            RuleFor(x => x.AllianceTierCode).MaximumLength(16).When(x => x.AllianceTierCode is not null);
        }
    }
}
