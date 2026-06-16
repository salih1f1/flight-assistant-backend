using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Flights
{
    /// <summary>Fetches live status from external providers and persists a FlightStatusEvent if new.</summary>
    /// <param name="TravelerId">When set, enforces that the traveler is tracking the flight. Pass null for admin/background callers.</param>
    public record SyncFlightStatusCommand(Guid? TravelerId, int FlightId) : IRequest<bool>; // returns true if a new event was recorded

    public sealed class SyncFlightStatusCommandHandler : IRequestHandler<SyncFlightStatusCommand, bool>
    {
        private readonly IFlightRepository _flights;
        private readonly IFlightStatusProvider _provider;

        public SyncFlightStatusCommandHandler(IFlightRepository flights, IFlightStatusProvider provider)
        {
            _flights = flights;
            _provider = provider;
        }

        public async Task<bool> Handle(SyncFlightStatusCommand cmd, CancellationToken ct)
        {
            if (cmd.TravelerId is { } travelerId)
            {
                var link = await _flights.GetTravelerFlightAsync(travelerId, cmd.FlightId, ct);
                if (link is null)
                    throw new DomainException("Not tracking this flight.", 403);
            }

            var flight = await _flights.GetFlightByIdAsync(cmd.FlightId, ct)
                ?? throw new DomainException($"Flight {cmd.FlightId} not found.", 404);

            // Build the flight number string: e.g. "BA123"
            // We stored flightNumber already normalised in the aggregate.
            var result = await _provider.GetStatusAsync(flight.FlightNumber, flight.ServiceDateUtc, ct);
            if (result is null) return false;

            // Look up the FlightStatus row id first, so the dedup hash uses the same id-based
            // formula as the stored hash (see FlightStatusEvent.ComputeDedupHash).
            var statusId = await _flights.GetFlightStatusIdByCodeAsync(result.StatusCode, ct)
                ?? throw new DomainException($"Unknown flight status code '{result.StatusCode}'.", 404);

            // Dedup check
            var dedupHash = FlightStatusEvent.ComputeDedupHash(flight.Id, statusId, result.EffectiveAtUtc, _provider.ProviderName, result.ExternalId);
            if (await _flights.FlightStatusEventExistsAsync(dedupHash, ct)) return false;

            var evt = FlightStatusEvent.Create(
                flight.Id,
                statusId,
                result.EffectiveAtUtc,
                _provider.ProviderName,
                result.ExternalId,
                result.Gate,
                result.Terminal,
                result.BaggageClaim,
                rawPayloadJson: result.RawPayloadJson);

            // ApplyStatusUpdate raises FlightStatusChanged domain event, which the
            // DomainEventDispatchInterceptor will publish after SaveChanges succeeds.
            flight.ApplyStatusUpdate(
                result.StatusCode,
                result.EstimatedDepartureUtc,
                result.EstimatedArrivalUtc,
                result.ActualDepartureUtc,
                result.ActualArrivalUtc,
                result.EquipmentIcao);

            await _flights.AddFlightStatusEventAsync(evt, ct);
            await _flights.SaveChangesAsync(ct);

            return true;
        }
    }

    public sealed class SyncFlightStatusCommandValidator : AbstractValidator<SyncFlightStatusCommand>
    {
        public SyncFlightStatusCommandValidator()
        {
            RuleFor(x => x.FlightId).GreaterThan(0);
        }
    }
}
