using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Airlines
{
    public record DeleteAirlineCommand(int Id) : IRequest;

    public sealed class DeleteAirlineCommandHandler : IRequestHandler<DeleteAirlineCommand>
    {
        private readonly IAirlineRepository _repo;
        private readonly IAdminFlightRepository _flights;

        public DeleteAirlineCommandHandler(IAirlineRepository repo, IAdminFlightRepository flights)
        {
            _repo = repo;
            _flights = flights;
        }

        public async Task Handle(DeleteAirlineCommand cmd, CancellationToken ct)
        {
            var airline = await _repo.GetByIdAsync(cmd.Id, ct);
            if (airline is null) return;

            var hasFlights = await _flights.AnyForAirlineAsync(cmd.Id, ct);
            if (hasFlights)
                throw new DomainException($"Cannot delete airline {cmd.Id}: it has associated flights.", 409);

            await _repo.DeleteAsync(airline, ct);
            await _repo.SaveChangesAsync(ct);
        }
    }

    public sealed class DeleteAirlineCommandValidator : AbstractValidator<DeleteAirlineCommand>
    {
        public DeleteAirlineCommandValidator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
        }
    }
}
