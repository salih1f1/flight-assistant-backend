using FlightAssistant.Application.Interfaces.Repositories;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Loyalty
{
    public record DeleteLoyaltyProgramCommand(int Id) : IRequest;

    public sealed class DeleteLoyaltyProgramCommandHandler : IRequestHandler<DeleteLoyaltyProgramCommand>
    {
        private readonly ILoyaltyRepository _repo;
        public DeleteLoyaltyProgramCommandHandler(ILoyaltyRepository repo) => _repo = repo;

        public async Task Handle(DeleteLoyaltyProgramCommand cmd, CancellationToken ct)
        {
            var program = await _repo.GetProgramByIdAsync(cmd.Id, ct);
            if (program is null) return;
            await _repo.DeleteProgramAsync(program, ct);
            await _repo.SaveChangesAsync(ct);
        }
    }

    public sealed class DeleteLoyaltyProgramCommandValidator : AbstractValidator<DeleteLoyaltyProgramCommand>
    {
        public DeleteLoyaltyProgramCommandValidator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
        }
    }
}
