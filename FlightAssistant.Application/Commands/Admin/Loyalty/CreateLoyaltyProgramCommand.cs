using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Loyalty
{
    public record CreateLoyaltyProgramCommand(
        string Code,
        string Name,
        int KindId,
        int? AirlineId,
        string? AllianceCode
    ) : IRequest<int>;

    public sealed class CreateLoyaltyProgramCommandHandler : IRequestHandler<CreateLoyaltyProgramCommand, int>
    {
        private readonly ILoyaltyRepository _repo;
        public CreateLoyaltyProgramCommandHandler(ILoyaltyRepository repo) => _repo = repo;

        public async Task<int> Handle(CreateLoyaltyProgramCommand cmd, CancellationToken ct)
        {
            var program = LoyaltyProgram.Create(cmd.Code, cmd.Name, cmd.KindId, cmd.AirlineId, cmd.AllianceCode);
            await _repo.AddProgramAsync(program, ct);
            await _repo.SaveChangesAsync(ct);
            return program.Id;
        }
    }

    public sealed class CreateLoyaltyProgramCommandValidator : AbstractValidator<CreateLoyaltyProgramCommand>
    {
        public CreateLoyaltyProgramCommandValidator()
        {
            RuleFor(x => x.Code).NotEmpty().MaximumLength(16);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.KindId).GreaterThan(0);
            RuleFor(x => x.AllianceCode).MaximumLength(8).When(x => x.AllianceCode is not null);
        }
    }
}
