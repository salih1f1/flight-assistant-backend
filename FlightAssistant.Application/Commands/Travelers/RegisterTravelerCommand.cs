using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Travelers.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Travelers
{
    public record RegisterTravelerCommand(
        Guid TravelerId,   // comes from B2C OID claim — idempotency key
        string Email,
        string? DisplayName,
        string? CountryCode
    ) : IRequest<Guid>;

    public sealed class RegisterTravelerCommandHandler : IRequestHandler<RegisterTravelerCommand, Guid>
    {
        private readonly ITravelerRepository _travelers;

        public RegisterTravelerCommandHandler(ITravelerRepository travelers) => _travelers = travelers;

        public async Task<Guid> Handle(RegisterTravelerCommand cmd, CancellationToken ct)
        {
            // Idempotent: return existing if already registered
            var existing = await _travelers.GetByEmailAsync(cmd.Email, ct);
            if (existing is not null) return existing.Id;

            var traveler = Traveler.Register(cmd.Email, cmd.DisplayName, cmd.CountryCode);
            await _travelers.AddAsync(traveler, ct);
            await _travelers.SaveChangesAsync(ct);
            return traveler.Id;
        }
    }
}
