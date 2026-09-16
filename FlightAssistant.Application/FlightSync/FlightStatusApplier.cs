using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.FlightSync
{
    /// <inheritdoc cref="IFlightStatusApplier"/>
    public sealed class FlightStatusApplier : IFlightStatusApplier
    {
        // The safe, always-seeded fallback code (see FlightStatusConfiguration.HasData) that
        // every provider maps its own unrecognised statuses to. An unknown code reaching this far
        // means a *provider* has emitted something outside that mapping — not a data problem — so
        // it degrades to the same fallback rather than aborting the sync.
        private const string UnknownStatusCode = "UNK";

        private readonly IFlightRepository _flights;
        private readonly ILogger<FlightStatusApplier> _logger;

        public FlightStatusApplier(IFlightRepository flights, ILogger<FlightStatusApplier> logger)
        {
            _flights = flights;
            _logger = logger;
        }

        /// <summary>
        /// A new status becomes an event, and the flight decides whether it pushes (#352); a status the
        /// flight already has goes to <see cref="ApplyKnownStatusAsync"/> (#351). Changes the flight only
        /// after every step that can throw, so a failure leaves nothing half-applied for the caller to save.
        /// </summary>
        public async Task<bool> ApplyAsync(
            Flight flight, FlightStatusResult result, string source, DateTime nowUtc, CancellationToken ct = default)
        {
            // Look up the FlightStatus row id first, so the dedup hash uses the same id-based
            // formula as the stored hash (see FlightStatusEvent.ComputeDedupHash).
            //
            // Every provider already maps its own unrecognised statuses to "UNK" (the seeded safe
            // fallback — see FlightStatusConfiguration.HasData), so a code that still fails to
            // resolve here means a provider has started emitting something outside that mapping.
            // That is a provider change to investigate, not a reason to lose this flight's status
            // update entirely (and discard the AeroAPI call already spent fetching it) — so this
            // degrades to "UNK" and logs a warning naming the code, rather than aborting.
            var resolvedStatusCode = result.StatusCode;
            var statusId = await _flights.GetFlightStatusIdByCodeAsync(resolvedStatusCode, ct);
            if (statusId is null)
            {
                _logger.LogWarning(
                    "Unrecognised flight status code {StatusCode} from provider {Provider} for flight {FlightId}; falling back to {Fallback}",
                    result.StatusCode, source, flight.Id, UnknownStatusCode);

                resolvedStatusCode = UnknownStatusCode;
                statusId = await _flights.GetFlightStatusIdByCodeAsync(UnknownStatusCode, ct)
                    ?? throw new DomainException(
                        $"Flight status code '{UnknownStatusCode}' is not seeded; the flight_statuses reference table is misconfigured.",
                        404);
            }

            var dedupHash = FlightStatusEvent.ComputeDedupHash(flight.Id, statusId.Value, result.EffectiveAtUtc, source, result.ExternalId);
            if (await _flights.FlightStatusEventExistsAsync(dedupHash, ct))
                return await ApplyKnownStatusAsync(flight, statusId.Value, result, source, nowUtc, ct);

            // The flight's current status is its most recently recorded event (#351). Whether the status changed
            // is one input to the push decision the flight makes (#352): a corrected actual departure, or a delay
            // that hasn't grown 15 minutes past the last delay push, records the event without a push. Read
            // before the flight changes.
            var latest = await _flights.GetLatestFlightStatusEventAsync(flight.Id, ct);

            var evt = FlightStatusEvent.Create(
                flight.Id,
                statusId.Value,
                result.EffectiveAtUtc,
                source,
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
                nowUtc,
                statusChanged: latest?.StatusId != statusId.Value,
                reportedScheduledDepartureUtc: result.ScheduledDepartureUtc);

            await _flights.AddFlightStatusEventAsync(evt, ct);
            return true;
        }

        /// <summary>
        /// The provider reported a status this flight already has (#351). The response used to be dropped.
        /// Now its times update the flight, and a newly reported gate, terminal or baggage claim is recorded
        /// as a detail-change event. Neither raises <c>FlightStatusChanged</c>, so neither sends a push. A
        /// detail the provider no longer reports keeps its last known value. A detail change is recorded
        /// only when the reported status is the latest event's. Returns whether an event was
        /// recorded. The flight changes only after the latest-event read, the step that can throw.
        /// </summary>
        private async Task<bool> ApplyKnownStatusAsync(
            Flight flight, int statusId, FlightStatusResult result, string source, DateTime nowUtc, CancellationToken ct)
        {
            var latest = await _flights.GetLatestFlightStatusEventAsync(flight.Id, ct);

            flight.ApplyProviderTimes(result.EstimatedDepartureUtc, result.EstimatedArrivalUtc,
                result.ActualDepartureUtc, result.ActualArrivalUtc, result.EquipmentIcao, nowUtc);

            // Only on top of the same status as the latest event. A response whose status matches an older
            // event but not the latest (a status that returned, or an out-of-order response) records nothing,
            // as before: a detail change under that status would relabel the current status with no push and
            // no CancelledAtUtc update.
            if (latest is null || latest.StatusId != statusId || !ReportsNewDetails(result, latest))
                return false;

            await _flights.AddFlightStatusEventAsync(FlightStatusEvent.CreateDetailChange(
                flight.Id, statusId, result.EffectiveAtUtc, source, result.ExternalId, latest.Id,
                gate: result.Gate ?? latest.Gate,
                terminal: result.Terminal ?? latest.Terminal,
                baggageClaim: result.BaggageClaim ?? latest.BaggageClaim,
                rawPayloadJson: result.RawPayloadJson), ct);
            return true;
        }

        /// <summary>A detail is new only when the provider reports a value that differs from the latest event's.</summary>
        private static bool ReportsNewDetails(FlightStatusResult result, FlightStatusEvent latest) =>
            (result.Gate is not null && result.Gate != latest.Gate)
            || (result.Terminal is not null && result.Terminal != latest.Terminal)
            || (result.BaggageClaim is not null && result.BaggageClaim != latest.BaggageClaim);
    }
}
