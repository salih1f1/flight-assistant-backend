using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.Commands.Flights
{
    /// <summary>Who asked for a sync. Only the background loop skips a Stopped flight.</summary>
    public enum SyncTrigger
    {
        Scheduled,
        Admin,
        Traveler
    }

    /// <summary>
    /// Syncs one flight when its sync mode calls for it, then stores when it's next due (#356).
    /// </summary>
    /// <param name="TravelerId">When set, enforces that the traveler is tracking the flight. Pass null for admin/background callers.</param>
    public record SyncFlightStatusCommand(Guid? TravelerId, int FlightId, SyncTrigger Trigger) : IRequest<bool>; // true if a new event was recorded

    public sealed class SyncFlightStatusCommandHandler : IRequestHandler<SyncFlightStatusCommand, bool>
    {
        // The safe, always-seeded fallback code (see FlightStatusConfiguration.HasData) that
        // every provider maps its own unrecognised statuses to. An unknown code reaching this far
        // means a *provider* has emitted something outside that mapping — not a data problem — so
        // it degrades to the same fallback rather than aborting the sync.
        private const string UnknownStatusCode = "UNK";

        private readonly IFlightRepository _flights;
        private readonly IFlightStatusProvider _provider;
        private readonly IFlightSyncOptions _syncOptions;
        private readonly IFlightStatusWindowOptions _windowOptions;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<SyncFlightStatusCommandHandler> _logger;

        public SyncFlightStatusCommandHandler(
            IFlightRepository flights,
            IFlightStatusProvider provider,
            IFlightSyncOptions syncOptions,
            IFlightStatusWindowOptions windowOptions,
            TimeProvider timeProvider,
            ILogger<SyncFlightStatusCommandHandler> logger)
        {
            _flights = flights;
            _provider = provider;
            _syncOptions = syncOptions;
            _windowOptions = windowOptions;
            _timeProvider = timeProvider;
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

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var options = FlightSyncPlanOptions.From(_syncOptions, _windowOptions);
            var isTracked = cmd.TravelerId is not null || await _flights.IsFlightTrackedAsync(flight.Id, ct);
            var plan = FlightSyncPlanner.Plan(flight, isTracked, now, options);

            switch (plan.Mode)
            {
                case null:
                    flight.ApplySyncPlan(null, null);
                    await _flights.SaveChangesAsync(ct);
                    return false;

                // The loop never calls for a finished flight (#345); a person asking still gets a call.
                case FlightSyncMode.Stopped when cmd.Trigger == SyncTrigger.Scheduled:
                    flight.ApplySyncPlan(FlightSyncMode.Stopped, null);
                    await _flights.SaveChangesAsync(ct);
                    return false;

                // AeroAPI can't answer before Live starts (#343). Step 2 of #349 adds a daily
                // /schedules check here; until then there is nothing to call.
                case FlightSyncMode.Schedule:
                    flight.ApplySyncPlan(FlightSyncMode.Schedule, FlightSyncPlanner.LiveStartsAtUtc(flight, options));
                    await _flights.SaveChangesAsync(ct);
                    return false;
            }

            var recorded = await ApplyLiveStatusAsync(flight, now, ct);

            // Any call that returned counts as a sync, even an empty or duplicate one. Re-plan from
            // the times the call may just have changed.
            flight.MarkSynced(now);
            var next = FlightSyncPlanner.Plan(flight, isTracked, now, options);
            flight.ApplySyncPlan(next.Mode, next.NextSyncDueUtc);

            await _flights.SaveChangesAsync(ct);
            return recorded;
        }

        /// <summary>
        /// Fetches the live status and, if it's new, records it. Changes the flight only after every
        /// step that can throw, so a failure leaves it untouched for the next attempt.
        /// </summary>
        private async Task<bool> ApplyLiveStatusAsync(Flight flight, DateTime now, CancellationToken ct)
        {
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

            // ApplyStatusUpdate raises FlightStatusChanged, which the DomainEventDispatchInterceptor
            // publishes after SaveChanges succeeds.
            flight.ApplyStatusUpdate(
                resolvedStatusCode,
                result.EstimatedDepartureUtc,
                result.EstimatedArrivalUtc,
                result.ActualDepartureUtc,
                result.ActualArrivalUtc,
                result.EquipmentIcao,
                now);

            await _flights.AddFlightStatusEventAsync(evt, ct);
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
