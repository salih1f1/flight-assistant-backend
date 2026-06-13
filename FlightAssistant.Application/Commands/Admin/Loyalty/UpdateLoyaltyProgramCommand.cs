using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Loyalty
{
    public record UpdateLoyaltyProgramCommand(
        int Id,
        string Code,
        string Name,
        int KindId,
        int? AirlineId,
        string? AllianceCode
    ) : IRequest;

    public sealed class UpdateLoyaltyProgramCommandHandler : IRequestHandler<UpdateLoyaltyProgramCommand>
    {
        private readonly ILoyaltyRepository _repo;
        public UpdateLoyaltyProgramCommandHandler(ILoyaltyRepository repo) => _repo = repo;

        public async Task Handle(UpdateLoyaltyProgramCommand cmd, CancellationToken ct)
        {
            var program = await _repo.GetProgramByIdAsync(cmd.Id, ct)
                ?? throw new DomainException($"Loyalty program {cmd.Id} not found.", 404);
            program.Update(cmd.Code, cmd.Name, cmd.KindId, cmd.AirlineId, cmd.AllianceCode);
            await _repo.SaveChangesAsync(ct);
        }
    }

    public sealed class UpdateLoyaltyProgramCommandValidator : AbstractValidator<UpdateLoyaltyProgramCommand>
    {
        public UpdateLoyaltyProgramCommandValidator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
            RuleFor(x => x.Code).NotEmpty().MaximumLength(16);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.KindId).GreaterThan(0);
            RuleFor(x => x.AllianceCode).MaximumLength(8).When(x => x.AllianceCode is not null);
        }
    }
}
