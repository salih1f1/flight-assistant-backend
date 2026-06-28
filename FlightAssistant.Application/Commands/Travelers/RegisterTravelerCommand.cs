using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Travelers.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Travelers
{
    public record RegisterTravelerCommand(
        Guid TravelerId,   // comes from B2C OID claim — idempotency key
        string Email,
        string? DisplayName
    ) : IRequest<Guid>;

    public sealed class RegisterTravelerCommandHandler : IRequestHandler<RegisterTravelerCommand, Guid>
    {
        private readonly ITravelerRepository _travelers;

        public RegisterTravelerCommandHandler(ITravelerRepository travelers) => _travelers = travelers;

        public async Task<Guid> Handle(RegisterTravelerCommand cmd, CancellationToken ct)
        {
            // Idempotent upsert keyed on the B2C oid. Called on every login, so it also
            // reconciles identity fields (email/name) from the Azure token (one-way sync).
            var existing = await _travelers.GetByIdAsync(cmd.TravelerId, ct);
            if (existing is not null)
            {
                if (existing.SyncFromIdentity(cmd.Email, cmd.DisplayName))
                    await _travelers.SaveChangesAsync(ct);
                return existing.Id;
            }

            var traveler = Traveler.Register(cmd.TravelerId, cmd.Email, cmd.DisplayName);
            await _travelers.AddAsync(traveler, ct);
            await _travelers.SaveChangesAsync(ct);
            return traveler.Id;
        }
    }
}
