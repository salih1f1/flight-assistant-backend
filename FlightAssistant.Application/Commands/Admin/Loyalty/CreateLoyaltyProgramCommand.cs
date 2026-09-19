using FlightAssistant.Application.Commands.Admin.Common;
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
        int? AirlineId
    ) : IRequest<int>;

    public sealed class CreateLoyaltyProgramCommandHandler : IRequestHandler<CreateLoyaltyProgramCommand, int>
    {
        private readonly ILoyaltyRepository _repo;
        private readonly IAirlineRepository _airlines;
        public CreateLoyaltyProgramCommandHandler(ILoyaltyRepository repo, IAirlineRepository airlines)
        {
            _repo = repo;
            _airlines = airlines;
        }

        public async Task<int> Handle(CreateLoyaltyProgramCommand cmd, CancellationToken ct)
        {
            await ReferenceExistence.EnsureLoyaltyProgramKindExistsAsync(_repo, cmd.KindId, ct);
            await ReferenceExistence.EnsureAirlineExistsAsync(_airlines, cmd.AirlineId, "LoyaltyProgram", ct);
            var program = LoyaltyProgram.Create(cmd.Code, cmd.Name, cmd.KindId, cmd.AirlineId);
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
        }
    }
}
