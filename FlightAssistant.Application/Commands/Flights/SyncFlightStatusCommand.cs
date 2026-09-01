using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.Commands.Flights
{
    /// <summary>Fetches live status from external providers and persists a FlightStatusEvent if new.</summary>
    /// <param name="TravelerId">When set, enforces that the traveler is tracking the flight. Pass null for admin/background callers.</param>
    public record SyncFlightStatusCommand(Guid? TravelerId, int FlightId) : IRequest<bool>; // returns true if a new event was recorded

    public sealed class SyncFlightStatusCommandHandler : IRequestHandler<SyncFlightStatusCommand, bool>
    {
        // The safe, always-seeded fallback code (see FlightStatusConfiguration.HasData) that
        // every provider maps its own unrecognised statuses to. An unknown code reaching this far
        // means a *provider* has emitted something outside that mapping — not a data problem — so
        // it degrades to the same fallback rather than aborting the sync.
        private const string UnknownStatusCode = "UNK";

        private readonly IFlightRepository _flights;
        private readonly IFlightStatusProvider _provider;
        private readonly ILogger<SyncFlightStatusCommandHandler> _logger;

        public SyncFlightStatusCommandHandler(
            IFlightRepository flights, IFlightStatusProvider provider, ILogger<SyncFlightStatusCommandHandler> logger)
        {
            _flights = flights;
            _provider = provider;
            _logger = logger;
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
            //
            // Every provider already maps its own unrecognised statuses to "UNK" (the seeded safe
            // fallback — see FlightStatusConfiguration.HasData), so a code that still fails to
            // resolve here means a provider has started emitting something outside that mapping.
            // That is a provider change to investigate, not a reason to lose this flight's status
            // update entirely (and discard the AeroAPI call already spent fetching it) — so this
            // degrades to "UNK" and logs a warning naming the code, rather than aborting the sync.
            var resolvedStatusCode = result.StatusCode;
            var statusId = await _flights.GetFlightStatusIdByCodeAsync(resolvedStatusCode, ct);
            if (statusId is null)
            {
                _logger.LogWarning(
                    "Unrecognised flight status code {StatusCode} from provider {Provider} for flight {FlightId}; falling back to {Fallback}",
                    result.StatusCode, _provider.ProviderName, flight.Id, UnknownStatusCode);

                resolvedStatusCode = UnknownStatusCode;
                statusId = await _flights.GetFlightStatusIdByCodeAsync(UnknownStatusCode, ct)
                    ?? throw new DomainException(
                        $"Flight status code '{UnknownStatusCode}' is not seeded; the flight_statuses reference table is misconfigured.",
                        404);
            }

            // Dedup check
            var dedupHash = FlightStatusEvent.ComputeDedupHash(flight.Id, statusId.Value, result.EffectiveAtUtc, _provider.ProviderName, result.ExternalId);
            if (await _flights.FlightStatusEventExistsAsync(dedupHash, ct)) return false;

            var evt = FlightStatusEvent.Create(
                flight.Id,
                statusId.Value,
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
                resolvedStatusCode,
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
