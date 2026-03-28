using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Commands.Travelers
{
    public record RemoveDeviceTokenCommand(Guid TravelerId, Guid DeviceTokenId) : IRequest;

    public sealed class RemoveDeviceTokenCommandHandler : IRequestHandler<RemoveDeviceTokenCommand>
    {
        private readonly ITravelerRepository _travelers;

        public RemoveDeviceTokenCommandHandler(ITravelerRepository travelers) => _travelers = travelers;

        public async Task Handle(RemoveDeviceTokenCommand cmd, CancellationToken ct)
        {
            var traveler = await _travelers.GetByIdAsync(cmd.TravelerId, ct)
                ?? throw new InvalidOperationException($"Traveler {cmd.TravelerId} not found.");

            traveler.RemoveDeviceToken(cmd.DeviceTokenId);
            await _travelers.SaveChangesAsync(ct);
        }
    }
}
