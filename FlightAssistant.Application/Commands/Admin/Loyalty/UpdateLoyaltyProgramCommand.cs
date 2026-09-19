using FlightAssistant.Application.Commands.Admin.Common;
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
        int? AirlineId
    ) : IRequest;

    public sealed class UpdateLoyaltyProgramCommandHandler : IRequestHandler<UpdateLoyaltyProgramCommand>
    {
        private readonly ILoyaltyRepository _repo;
        private readonly IAirlineRepository _airlines;
        public UpdateLoyaltyProgramCommandHandler(ILoyaltyRepository repo, IAirlineRepository airlines)
        {
            _repo = repo;
            _airlines = airlines;
        }

        public async Task Handle(UpdateLoyaltyProgramCommand cmd, CancellationToken ct)
        {
            var program = await _repo.GetProgramByIdAsync(cmd.Id, ct)
                ?? throw new DomainException($"Loyalty program {cmd.Id} not found.", 404);
            await ReferenceExistence.EnsureLoyaltyProgramKindExistsAsync(_repo, cmd.KindId, ct);
            await ReferenceExistence.EnsureAirlineExistsAsync(_airlines, cmd.AirlineId, "LoyaltyProgram", ct);
            program.Update(cmd.Code, cmd.Name, cmd.KindId, cmd.AirlineId);
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
        }
    }
}
