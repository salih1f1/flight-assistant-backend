using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Flights
{
    /// <summary>Fetches live status from external providers and persists a FlightStatusEvent if new.</summary>
    public record SyncFlightStatusCommand(int FlightId) : IRequest<bool>; // returns true if a new event was recorded

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
            var flight = await _flights.GetFlightByIdAsync(cmd.FlightId, ct)
                ?? throw new InvalidOperationException($"Flight {cmd.FlightId} not found.");

            // Build the flight number string: e.g. "BA123"
            // We stored flightNumber already normalised in the aggregate.
            var result = await _provider.GetStatusAsync(flight.FlightNumber, flight.ServiceDateUtc, ct);
            if (result is null) return false;

            // Dedup check
            var dedupHash = FlightStatusEvent.ComputeDedupHash(flight.Id, result.StatusCode, result.EffectiveAtUtc, _provider.ProviderName, result.ExternalId);
            if (await _flights.FlightStatusEventExistsAsync(dedupHash, ct)) return false;

            // Look up the FlightStatus row id
            var statusId = await _flights.GetFlightStatusIdByCodeAsync(result.StatusCode, ct)
                ?? throw new InvalidOperationException($"Unknown flight status code '{result.StatusCode}'.");

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

            flight.ApplyStatusUpdate(
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
}
