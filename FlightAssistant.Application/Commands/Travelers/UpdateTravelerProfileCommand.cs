using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using MediatR;

namespace FlightAssistant.Application.Commands.Travelers
{
    // DisplayName and CountryCode are owned by Azure AD B2C (source of truth) and are
    // reconciled on login via Traveler.SyncFromIdentity — they are intentionally not editable here.
    public record UpdateTravelerProfileCommand(
        Guid TravelerId,
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
                ?? throw new DomainException($"Traveler {cmd.TravelerId} not found.", 404);

            if (cmd.NationalityCountryCode is not null)
                traveler.UpdatePassportCountry(cmd.NationalityCountryCode);

            if (cmd.ResidenceCountryCode is not null)
                traveler.UpdateResidenceCountry(cmd.ResidenceCountryCode);

            await _travelers.SaveChangesAsync(ct);
        }
    }
}
