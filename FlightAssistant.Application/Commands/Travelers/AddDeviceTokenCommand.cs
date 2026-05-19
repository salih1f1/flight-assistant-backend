using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using MediatR;

namespace FlightAssistant.Application.Commands.Travelers
{
    public record AddDeviceTokenCommand(Guid TravelerId, string Platform, string Token) : IRequest;

    public sealed class AddDeviceTokenCommandHandler : IRequestHandler<AddDeviceTokenCommand>
    {
        private readonly ITravelerRepository _travelers;

        public AddDeviceTokenCommandHandler(ITravelerRepository travelers) => _travelers = travelers;

        public async Task Handle(AddDeviceTokenCommand cmd, CancellationToken ct)
        {
            var traveler = await _travelers.GetByIdAsync(cmd.TravelerId, ct)
                ?? throw new DomainException($"Traveler {cmd.TravelerId} not found.", 404);

            traveler.AddDeviceToken(cmd.Platform, cmd.Token);
            await _travelers.SaveChangesAsync(ct);
        }
    }
}
