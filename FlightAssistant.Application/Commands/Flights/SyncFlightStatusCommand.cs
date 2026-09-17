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
    /// What one flight's sync did (#349 §2.3). Before #443 all six of these came back as a single
    /// bool, so a caller could not tell a flight nobody tracks from one the provider has no row for
    /// yet from a call that ran and found nothing new — the admin panel needs them apart to say what
    /// happened.
    /// <para>
    /// Named for the status sync rather than <c>FlightSyncOutcome</c>, which is taken by the API
    /// layer's <c>FlightSyncHeartbeat</c> and means something different: that one describes a whole
    /// background <em>cycle</em> (NeverRun/Running/Success/Failed), this one a single flight.
    /// </para>
    /// </summary>
    public enum FlightStatusSyncOutcome
    {
        /// <summary>A live call recorded a new status event.</summary>
        StatusUpdated,

        /// <summary>A call ran and found nothing new, or a Stopped flight was skipped by the loop.</summary>
        NoChange,

        /// <summary>A live call returned no row for the service date.</summary>
        NoDataYet,

        /// <summary>The schedule check found changed scheduled times and saved them. Unreachable
        /// until #349's step 2 (#444) implements the check.</summary>
        ScheduleUpdated,

        /// <summary>The schedule check didn't find the flight. Unreachable until #444, as above.</summary>
        NotInSchedule,

        /// <summary>No traveler tracks the flight, so no call was made and the plan was cleared.</summary>
        NotTracked
    }

    /// <summary>
    /// The outcome of a sync plus the plan it left behind, so a caller can report what happened and
    /// when the next sync is due without re-reading the flight.
    /// </summary>
    /// <param name="ScheduleMissingSinceUtc">When the flight first went missing from the schedule;
    /// always null until #444 implements the check that sets it.</param>
    public record FlightSyncResult(
        FlightStatusSyncOutcome Outcome,
        DateTime? LastSyncedUtc,
        DateTime? NextSyncDueUtc,
        FlightSyncMode? SyncMode,
        DateTime? ScheduleMissingSinceUtc);

    /// <summary>
    /// Syncs one flight when its sync mode calls for it, then stores when it's next due (#356). Holds
    /// back calls as this month's AeroAPI spend nears the budget (#303).
    /// </summary>
    /// <param name="TravelerId">When set, enforces that the traveler is tracking the flight. Pass null for admin/background callers.</param>
    public record SyncFlightStatusCommand(Guid? TravelerId, int FlightId, SyncTrigger Trigger) : IRequest<FlightSyncResult>;

    public sealed class SyncFlightStatusCommandHandler : IRequestHandler<SyncFlightStatusCommand, FlightSyncResult>
    {
        // Set just before a traveler's sync calls the provider; see the cooldown guard in Handle.
        private const string TravelerAttemptKeyPrefix = "flight-sync:traveler-attempt:";

        private readonly IFlightRepository _flights;
        private readonly IFlightStatusProvider _provider;
        private readonly IFlightStatusApplier _applier;
        private readonly IProviderBudgetGuard _budget;
        private readonly ICacheService _cache;
        private readonly IFlightSyncOptions _syncOptions;
        private readonly IFlightStatusWindowOptions _windowOptions;
        private readonly TimeProvider _timeProvider;
        private readonly IFlightSearchProvider _search;
        private readonly IRefDataRepository _refData;
        private readonly ILogger<SyncFlightStatusCommandHandler> _logger;

        public SyncFlightStatusCommandHandler(
            IFlightRepository flights,
            IFlightStatusProvider provider,
            IFlightStatusApplier applier,
            IProviderBudgetGuard budget,
            ICacheService cache,
            IFlightSyncOptions syncOptions,
            IFlightStatusWindowOptions windowOptions,
            TimeProvider timeProvider,
            IFlightSearchProvider search,
            IRefDataRepository refData,
            ILogger<SyncFlightStatusCommandHandler> logger)
        {
            _flights = flights;
            _provider = provider;
            _applier = applier;
            _budget = budget;
            _cache = cache;
            _syncOptions = syncOptions;
            _windowOptions = windowOptions;
            _timeProvider = timeProvider;
            _search = search;
            _refData = refData;
            _logger = logger;
        }

        public async Task<FlightSyncResult> Handle(SyncFlightStatusCommand cmd, CancellationToken ct)
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
                return Result(flight, FlightStatusSyncOutcome.NoChange);

            // A traveler pulling to refresh gets what the last sync stored: every call is billed, and
            // the loop already syncs a flight near departure every few minutes (#369). It reads the
            // flight's own LastSyncedUtc, so every traveler tracking the flight shares the cooldown.
            // A traveler's call starts the cooldown when it's attempted, not when it returns, so a failed
            // call or a second request during the throttle wait doesn't make another billed call.
            var travelerCooldown = cmd.Trigger == SyncTrigger.Traveler && _syncOptions.ManualSyncCooldown > TimeSpan.Zero;
            if (travelerCooldown &&
                (flight.LastSyncedUtc > now - _syncOptions.ManualSyncCooldown || await TravelerAttemptMarkedAsync(flight.Id, ct)))
                return Result(flight, FlightStatusSyncOutcome.NoChange);

            var options = FlightSyncPlanOptions.From(_syncOptions, _windowOptions);
            var isTracked = cmd.TravelerId is not null || await _flights.IsFlightTrackedAsync(flight.Id, ct);
            var plan = FlightSyncPlanner.Plan(flight, isTracked, now, options);

            switch (plan.Mode)
            {
                case null:
                    flight.ApplySyncPlan(null, null);
                    await _flights.SaveChangesAsync(ct);
                    return Result(flight, FlightStatusSyncOutcome.NotTracked);

                // The loop never calls for a finished flight (#345); a person asking still gets a call.
                case FlightSyncMode.Stopped when cmd.Trigger == SyncTrigger.Scheduled:
                    flight.ApplySyncPlan(FlightSyncMode.Stopped, null);
                    await _flights.SaveChangesAsync(ct);
                    return Result(flight, FlightStatusSyncOutcome.NoChange);

                // AeroAPI can't answer a live-status call before Live starts (#343), so this far out the
                // check is against the published schedule instead (#444, §2.4) — which catches a retime
                // before the flight ever reaches the live window.
                case FlightSyncMode.Schedule:
                {
                    var scheduleOutcome = await CheckScheduleAsync(flight, now, ct);
                    flight.MarkSynced(now);
                    flight.ApplySyncPlan(FlightSyncMode.Schedule, FlightSyncPlanner.LiveStartsAtUtc(flight, options));
                    await _flights.SaveChangesAsync(ct);
                    return Result(flight, scheduleOutcome);
                }
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
                return Result(flight, FlightStatusSyncOutcome.NoChange);
            }

            var kind = cmd.Trigger == SyncTrigger.Scheduled ? StatusCallKind.ScheduledPoll : StatusCallKind.Interactive;

            FlightStatusSyncOutcome outcome;
            try
            {
                outcome = await ApplyLiveStatusAsync(flight, now, kind, ct);
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
            return Result(flight, outcome);
        }

        /// <summary>
        /// The daily schedule check (#444, §2.4). Searches by flight number and service date — never by
        /// route, which would send the search to a provider that resolves a marketing number to its
        /// operator and could match a different flight entirely (#422) — then looks for a row that also
        /// departs the airport this flight departs.
        ///
        /// <para>
        /// A row for the same number on a different route is not this flight: AeroAPI answers a
        /// flight-number search with every operation of that number, so matching on the number alone
        /// would adopt another airport's times.
        /// </para>
        /// </summary>
        private async Task<FlightStatusSyncOutcome> CheckScheduleAsync(Flight flight, DateTime now, CancellationToken ct)
        {
            var departureIata = await _refData.GetAirportIataByIdAsync(flight.DepartureAirportId, ct);
            var criteria = new FlightSearchCriteria(null, null, flight.ServiceDateUtc, flight.FlightNumber);
            var rows = await _search.SearchAsync(criteria, ct);

            var match = rows.FirstOrDefault(r =>
                string.Equals(r.FlightNumber, flight.FlightNumber, StringComparison.OrdinalIgnoreCase)
                && r.ServiceDate == flight.ServiceDateUtc
                && departureIata is not null
                && string.Equals(r.DepartureAirportIata, departureIata, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                flight.MarkMissingFromSchedule(now);
                return FlightStatusSyncOutcome.NotInSchedule;
            }

            return flight.ApplyScheduleUpdate(match.ScheduledDepartureUtc, match.ScheduledArrivalUtc, now)
                ? FlightStatusSyncOutcome.ScheduleUpdated
                : FlightStatusSyncOutcome.NoChange;
        }

        /// <summary>The outcome plus the plan the handler just stored, read from the flight itself so
        /// the two can never disagree.</summary>
        private static FlightSyncResult Result(Flight flight, FlightStatusSyncOutcome outcome) =>
            new(outcome, flight.LastSyncedUtc, flight.NextSyncDueUtc, flight.SyncMode, flight.ScheduleMissingSinceUtc);

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
        /// Fetches the live status and hands it to <see cref="IFlightStatusApplier"/>, which records it and
        /// decides the push (#351, #352). Returns whether an event was recorded; a response with no matching
        /// row records nothing.
        /// </summary>
        private async Task<FlightStatusSyncOutcome> ApplyLiveStatusAsync(Flight flight, DateTime now, StatusCallKind kind, CancellationToken ct)
        {
            var result = await _provider.GetStatusAsync(flight.FlightNumber, flight.ServiceDateUtc, kind, ct);
            // The call ran and the provider has no row for this service date yet — a different thing
            // from a call that ran and found nothing new, which the applier reports below.
            if (result is null) return FlightStatusSyncOutcome.NoDataYet;

            return await _applier.ApplyAsync(flight, result, _provider.ProviderName, now, ct)
                ? FlightStatusSyncOutcome.StatusUpdated
                : FlightStatusSyncOutcome.NoChange;
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
