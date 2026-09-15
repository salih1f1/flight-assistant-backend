using System.Globalization;
using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.ProviderBudget;
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
    /// Syncs one flight when its sync mode calls for it, then stores when it's next due (#356). Holds
    /// back calls as this month's AeroAPI spend nears the budget (#303).
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

        // Set just before a traveler's sync calls the provider; see the cooldown guard in Handle.
        private const string TravelerAttemptKeyPrefix = "flight-sync:traveler-attempt:";

        private readonly IFlightRepository _flights;
        private readonly IFlightStatusProvider _provider;
        private readonly IProviderBudgetGuard _budget;
        private readonly ICacheService _cache;
        private readonly IFlightSyncOptions _syncOptions;
        private readonly IFlightStatusWindowOptions _windowOptions;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<SyncFlightStatusCommandHandler> _logger;

        public SyncFlightStatusCommandHandler(
            IFlightRepository flights,
            IFlightStatusProvider provider,
            IProviderBudgetGuard budget,
            ICacheService cache,
            IFlightSyncOptions syncOptions,
            IFlightStatusWindowOptions windowOptions,
            TimeProvider timeProvider,
            ILogger<SyncFlightStatusCommandHandler> logger)
        {
            _flights = flights;
            _provider = provider;
            _budget = budget;
            _cache = cache;
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

            // The loop lists due flights, then syncs them 12 s apart; a manual sync in between may have
            // moved this one's next sync on, and the loop's call would be wasted.
            if (cmd.Trigger == SyncTrigger.Scheduled && flight.SyncMode is not null && flight.NextSyncDueUtc > now)
                return false;

            // A traveler pulling to refresh gets what the last sync stored: every call is billed, and
            // the loop already syncs a flight near departure every few minutes (#369). It reads the
            // flight's own LastSyncedUtc, so every traveler tracking the flight shares the cooldown.
            // A traveler's call starts the cooldown when it's attempted, not when it returns, so a failed
            // call or a second request during the throttle wait doesn't make another billed call.
            var travelerCooldown = cmd.Trigger == SyncTrigger.Traveler && _syncOptions.ManualSyncCooldown > TimeSpan.Zero;
            if (travelerCooldown &&
                (flight.LastSyncedUtc > now - _syncOptions.ManualSyncCooldown || await TravelerAttemptMarkedAsync(flight.Id, ct)))
                return false;

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

            // Before the budget read, which can wait on the AeroAPI throttle, so a second traveler request
            // during that wait finds the marker (#369).
            if (travelerCooldown)
                await MarkTravelerAttemptAsync(flight.Id, now, ct);

            // #303: a flight in Fast mode always gets its call and never reads the budget. Past the soft
            // limit, a Live flight's polls run at twice the interval. Once the budget is used up, the loop
            // makes no call for it, and a person's sync is told why rather than handed stored status.
            var budget = plan.Mode == FlightSyncMode.Fast
                ? ProviderBudgetState.Normal
                : await _budget.GetStateAsync(ct);
            if (budget != ProviderBudgetState.Normal)
            {
                options = options with { LiveInterval = options.LiveInterval * 2 };
                plan = FlightSyncPlanner.Plan(flight, isTracked, now, options);
            }

            if (budget == ProviderBudgetState.Exhausted)
            {
                if (cmd.Trigger != SyncTrigger.Scheduled)
                {
                    // No call was made, so the traveler's cooldown doesn't start.
                    if (travelerCooldown)
                        await ClearTravelerAttemptAsync(flight.Id, ct);

                    throw new DomainException(
                        "Flight status can't be refreshed right now: this month's flight data budget is used up. Please try again later.",
                        503);
                }

                flight.ApplySyncPlan(plan.Mode, plan.NextSyncDueUtc);
                await _flights.SaveChangesAsync(ct);
                return false;
            }

            var kind = cmd.Trigger == SyncTrigger.Scheduled ? StatusCallKind.ScheduledPoll : StatusCallKind.Interactive;

            bool recorded;
            try
            {
                recorded = await ApplyLiveStatusAsync(flight, now, kind, ct);
            }
            // A failed call can still be billed, so the loop waits one interval of the flight's mode
            // rather than retrying it every loop. A person's sync saves nothing.
            catch (Exception ex) when (cmd.Trigger == SyncTrigger.Scheduled &&
                                       !(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                await DeferNextSyncAsync(flight, plan, ct);
                throw;
            }

            // Any call that returned counts as a sync, even an empty or duplicate one. Re-plan from
            // the times the call may just have changed.
            flight.MarkSynced(now);
            var next = FlightSyncPlanner.Plan(flight, isTracked, now, options);
            flight.ApplySyncPlan(next.Mode, next.NextSyncDueUtc);

            await _flights.SaveChangesAsync(ct);
            return recorded;
        }

        /// <summary>
        /// Stores the pre-call plan (now + the mode's interval, capped) without marking the flight
        /// synced. A failed save is only logged, so the caller still gets the original error.
        /// </summary>
        private async Task DeferNextSyncAsync(Flight flight, FlightSyncPlan plan, CancellationToken ct)
        {
            flight.ApplySyncPlan(plan.Mode, plan.NextSyncDueUtc);
            try
            {
                await _flights.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Couldn't defer the next sync of flight {FlightId} after a failed sync", flight.Id);
            }
        }

        /// <summary>
        /// Whether a traveler's sync of this flight was attempted within the cooldown. A cache failure
        /// reads as no marker, so the LastSyncedUtc cooldown and the rate limit still apply.
        /// </summary>
        private async Task<bool> TravelerAttemptMarkedAsync(int flightId, CancellationToken ct)
        {
            try
            {
                // A string, because GetAsync returns default for a missing value type.
                return await _cache.GetAsync<string>($"{TravelerAttemptKeyPrefix}{flightId}", ct) is not null;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Couldn't read the manual-sync marker of flight {FlightId}; syncing without it", flightId);
                return false;
            }
        }

        /// <summary>Marks a traveler's call as attempted until the cooldown ends. A cache failure is
        /// only logged, so it never blocks the sync.</summary>
        private async Task MarkTravelerAttemptAsync(int flightId, DateTime now, CancellationToken ct)
        {
            try
            {
                await _cache.SetAsync($"{TravelerAttemptKeyPrefix}{flightId}", now.ToString("O", CultureInfo.InvariantCulture),
                    _syncOptions.ManualSyncCooldown, ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Couldn't set the manual-sync marker of flight {FlightId}; syncing without it", flightId);
            }
        }

        /// <summary>Removes the traveler's attempt marker when no call was made. A cache failure is only
        /// logged: the traveler then waits out the cooldown without a call.</summary>
        private async Task ClearTravelerAttemptAsync(int flightId, CancellationToken ct)
        {
            try
            {
                await _cache.RemoveAsync($"{TravelerAttemptKeyPrefix}{flightId}", ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Couldn't clear the manual-sync marker of flight {FlightId} after the budget refused the sync", flightId);
            }
        }

        /// <summary>
        /// Fetches the live status and records it. A new status becomes an event and raises the push; a
        /// status the flight already has goes to <see cref="ApplyKnownStatusAsync"/> (#351). Changes the
        /// flight only after every step that can throw, so a failure leaves nothing half-applied for the
        /// deferral to save.
        /// </summary>
        private async Task<bool> ApplyLiveStatusAsync(Flight flight, DateTime now, StatusCallKind kind, CancellationToken ct)
        {
            var result = await _provider.GetStatusAsync(flight.FlightNumber, flight.ServiceDateUtc, kind, ct);
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
            if (await _flights.FlightStatusEventExistsAsync(dedupHash, ct))
                return await ApplyKnownStatusAsync(flight, statusId.Value, result, now, ct);

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

        /// <summary>
        /// The provider reported a status this flight already has (#351). The response used to be dropped.
        /// Now its times update the flight, and a newly reported gate, terminal or baggage claim is recorded
        /// as a detail-change event. Neither raises <c>FlightStatusChanged</c>, so neither sends a push. A
        /// detail the provider no longer reports keeps its last known value. A detail change is recorded
        /// only when the reported status is the latest event's. Returns whether an event was
        /// recorded. The flight changes only after the latest-event read, the step that can throw.
        /// </summary>
        private async Task<bool> ApplyKnownStatusAsync(
            Flight flight, int statusId, FlightStatusResult result, DateTime now, CancellationToken ct)
        {
            var latest = await _flights.GetLatestFlightStatusEventAsync(flight.Id, ct);

            flight.ApplyProviderTimes(result.EstimatedDepartureUtc, result.EstimatedArrivalUtc,
                result.ActualDepartureUtc, result.ActualArrivalUtc, result.EquipmentIcao, now);

            // Only on top of the same status as the latest event. A response whose status matches an older
            // event but not the latest (a status that returned, or an out-of-order response) records nothing,
            // as before: a detail change under that status would relabel the current status with no push and
            // no CancelledAtUtc update.
            if (latest is null || latest.StatusId != statusId || !ReportsNewDetails(result, latest))
                return false;

            await _flights.AddFlightStatusEventAsync(FlightStatusEvent.CreateDetailChange(
                flight.Id, statusId, result.EffectiveAtUtc, _provider.ProviderName, result.ExternalId, latest.Id,
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

    public sealed class SyncFlightStatusCommandValidator : AbstractValidator<SyncFlightStatusCommand>
    {
        public SyncFlightStatusCommandValidator()
        {
            RuleFor(x => x.FlightId).GreaterThan(0);
        }
    }
}
