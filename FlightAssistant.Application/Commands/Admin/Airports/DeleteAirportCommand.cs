using FlightAssistant.Application.Interfaces.Repositories;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Airports
{
    public record DeleteAirportCommand(int Id) : IRequest;

    public sealed class DeleteAirportCommandHandler : IRequestHandler<DeleteAirportCommand>
    {
        private readonly IAirportRepository _repo;
        public DeleteAirportCommandHandler(IAirportRepository repo) => _repo = repo;

        public async Task Handle(DeleteAirportCommand cmd, CancellationToken ct)
        {
            var airport = await _repo.GetByIdAsync(cmd.Id, ct);
            if (airport is null) return;
            await _repo.DeleteAsync(airport, ct);
            await _repo.SaveChangesAsync(ct);
        }
    }

    public sealed class DeleteAirportCommandValidator : AbstractValidator<DeleteAirportCommand>
    {
        public DeleteAirportCommandValidator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
        }
    }
}
