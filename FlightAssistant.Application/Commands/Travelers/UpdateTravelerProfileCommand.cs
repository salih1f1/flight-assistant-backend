using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Commands.Travelers
{
    public record UpdateTravelerProfileCommand(
        Guid TravelerId,
        string? DisplayName,
        string? CountryCode,
        string? NationalityCountryCode,
        string? ResidenceCountryCode
    ) : IRequest;

    public sealed class UpdateTravelerProfileCommandHandler : IRequestHandler<UpdateTravelerProfileCommand>
    {
        private readonly ITravelerRepository _travelers;

        public UpdateTravelerProfileCommandHandler(ITravelerRepository travelers) => _travelers = travelers;

        public async Task Handle(UpdateTravelerProfileCommand cmd, CancellationToken ct)
        {
            var traveler = await _travelers.GetByIdAsync(cmd.TravelerId, ct)
                ?? throw new InvalidOperationException($"Traveler {cmd.TravelerId} not found.");

            traveler.UpdateProfile(cmd.DisplayName, cmd.CountryCode);

            if (cmd.NationalityCountryCode is not null)
                traveler.UpdatePassportCountry(cmd.NationalityCountryCode);

            if (cmd.ResidenceCountryCode is not null)
                traveler.UpdateResidenceCountry(cmd.ResidenceCountryCode);

            await _travelers.SaveChangesAsync(ct);
        }
    }
}
