using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Loyalty
{
    public record CreateLoyaltyTierCommand(
        int ProgramId,
        string Code,
        string Name,
        int Rank,
        string? AllianceCode,
        string? AllianceTierCode
    ) : IRequest<int>;

    public sealed class CreateLoyaltyTierCommandHandler : IRequestHandler<CreateLoyaltyTierCommand, int>
    {
        private readonly ILoyaltyRepository _repo;
        public CreateLoyaltyTierCommandHandler(ILoyaltyRepository repo) => _repo = repo;

        public async Task<int> Handle(CreateLoyaltyTierCommand cmd, CancellationToken ct)
        {
            var program = LoyaltyTierGuard.EnsureProgram(await _repo.GetProgramByIdAsync(cmd.ProgramId, ct), cmd.ProgramId);
            LoyaltyTierGuard.EnsureNoClash(program, cmd.Code, cmd.Rank);

            var tier = LoyaltyTier.Create(program.Id, cmd.Code, cmd.Name, cmd.Rank, cmd.AllianceCode, cmd.AllianceTierCode);
            // The program owns its tiers, so adding through the aggregate is the whole write: the
            // tracked collection carries the new row, and the database assigns its id on save.
            program.AddTier(tier);
            await _repo.SaveChangesAsync(ct);
            return tier.Id;
        }
    }

    public sealed class CreateLoyaltyTierCommandValidator : AbstractValidator<CreateLoyaltyTierCommand>
    {
        public CreateLoyaltyTierCommandValidator()
        {
            // The lengths LoyaltyTierConfiguration gives the columns.
            RuleFor(x => x.ProgramId).GreaterThan(0);
            RuleFor(x => x.Code).NotEmpty().MaximumLength(24);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Rank).GreaterThanOrEqualTo(0);
            RuleFor(x => x.AllianceCode).MaximumLength(8).When(x => x.AllianceCode is not null);
            RuleFor(x => x.AllianceTierCode).MaximumLength(16).When(x => x.AllianceTierCode is not null);
        }
    }
}
