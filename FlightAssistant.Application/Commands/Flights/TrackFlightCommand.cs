using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.ReferenceData;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using FlightAssistant.Domain.Verification.Enums;
using MediatR;

namespace FlightAssistant.Application.Commands.Flights
{
    public record TrackFlightCommand(
        Guid TravelerId,
        string FlightNumber,
        DateOnly ServiceDate,
        string DepartureAirportIata,
        string ArrivalAirportIata,
        DateTime ScheduledDepartureUtc,
        DateTime ScheduledArrivalUtc
    ) : IRequest<int>; // returns FlightId

    public sealed class TrackFlightCommandHandler : IRequestHandler<TrackFlightCommand, int>
    {
        private readonly IFlightRepository _flights;
        private readonly IRefDataRepository _refData;
        private readonly IReferenceDataProvisioner _provisioner;
        private readonly IFlightSyncOptions _syncOptions;
        private readonly IFlightStatusWindowOptions _windowOptions;
        private readonly TimeProvider _timeProvider;

        public TrackFlightCommandHandler(
            IFlightRepository flights,
            IRefDataRepository refData,
            IReferenceDataProvisioner provisioner,
            IFlightSyncOptions syncOptions,
            IFlightStatusWindowOptions windowOptions,
            TimeProvider timeProvider)
        {
            _flights = flights;
            _refData = refData;
            _provisioner = provisioner;
            _syncOptions = syncOptions;
            _windowOptions = windowOptions;
            _timeProvider = timeProvider;
        }

        public async Task<int> Handle(TrackFlightCommand cmd, CancellationToken ct)
        {
            // An IATA flight designator's first two characters are the marketing airline's IATA
            // code (e.g. "6E4120" -> "6E") — there is no separate AirlineIata field to trust or
            // to contradict it, so this is now the single source of truth for which airline is
            // being tracked.
            var airlineIata = cmd.FlightNumber.Length >= 2 ? cmd.FlightNumber[..2] : cmd.FlightNumber;

            var airlineId = await _refData.GetAirlineIdByIataAsync(airlineIata, ct);
            var depAirportId = await _refData.GetAirportIdByIataAsync(cmd.DepartureAirportIata, ct);
            var arrAirportId = await _refData.GetAirportIdByIataAsync(cmd.ArrivalAirportIata, ct);

            var now = _timeProvider.GetUtcNow().UtcDateTime;

            if (airlineId is null || depAirportId is null || arrAirportId is null)
            {
                // The cap is checked before provisioning so a refused track still writes nothing
                // (#465). This deliberately skips AlreadyTracksAsync -- airlineId may be null here.
                // Accepted cost: an at-cap traveler who already tracks this flight but named an
                // unresolvable airport gets 409 here instead of the idempotent success further down;
                // not a regression, since that request 404'd before the cap check was ever reached
                // pre-#465.
                var capBeforeProvisioning = _syncOptions.MaxActiveTrackedFlightsPerTraveler;
                if (await _flights.CountActiveTrackedFlightsAsync(
                        cmd.TravelerId, now, _syncOptions.StopAfterArrival, _syncOptions.StopWithoutArrivalAfter, ct)
                    >= capBeforeProvisioning)
                {
                    throw CapExceeded(capBeforeProvisioning);
                }

                var missing = new List<MissingReference>(3);
                if (airlineId is null)
                    missing.Add(new MissingReference(VerificationEntityType.Airline, airlineIata));
                if (depAirportId is null)
                    missing.Add(new MissingReference(VerificationEntityType.Airport, cmd.DepartureAirportIata));
                if (arrAirportId is null)
                    missing.Add(new MissingReference(VerificationEntityType.Airport, cmd.ArrivalAirportIata));

                var provision = await _provisioner.ProvisionAsync(missing, ct);
                if (provision.Status != ReferenceProvisionStatus.Provisioned)
                    throw ToDomainException(provision);

                // Re-resolve through our own DbContext: the provisioner wrote in a scope of its own.
                airlineId = await _refData.GetAirlineIdByIataAsync(airlineIata, ct)
                    ?? throw ReferenceNotFound(VerificationEntityType.Airline, airlineIata);
                depAirportId = await _refData.GetAirportIdByIataAsync(cmd.DepartureAirportIata, ct)
                    ?? throw ReferenceNotFound(VerificationEntityType.Airport, cmd.DepartureAirportIata);
                arrAirportId = await _refData.GetAirportIdByIataAsync(cmd.ArrivalAirportIata, ct)
                    ?? throw ReferenceNotFound(VerificationEntityType.Airport, cmd.ArrivalAirportIata);
            }

            var flightNumber = cmd.FlightNumber.Trim().ToUpperInvariant();

            // A cap on flights the traveler tracks that the status sync still polls (#380). A flight holds
            // its slot until the planner would stop it, so this bounds how many flights one account keeps
            // polled; it isn't a rate limit. Checked after the airline and airport lookups (an unknown one
            // is still a 404) and before the flight row exists, so a refused request writes nothing. A
            // flight the traveler already tracks still succeeds, so tracking stays idempotent.
            var cap = _syncOptions.MaxActiveTrackedFlightsPerTraveler;
            if (await _flights.CountActiveTrackedFlightsAsync(
                    cmd.TravelerId, now, _syncOptions.StopAfterArrival, _syncOptions.StopWithoutArrivalAfter, ct) >= cap
                && !await AlreadyTracksAsync(cmd.TravelerId, airlineId.Value, flightNumber, cmd.ServiceDate, ct))
            {
                throw CapExceeded(cap);
            }

            // Look up or create the Flight aggregate (race-safe via the unique ix_flight_lookup index)
            var candidate = Flight.Create(airlineId.Value, flightNumber, depAirportId.Value, arrAirportId.Value, cmd.ServiceDate, cmd.ScheduledDepartureUtc, cmd.ScheduledArrivalUtc);
            var flight = await _flights.GetOrCreateFlightAsync(candidate, ct);

            // Idempotent: skip if traveler already tracking this flight
            var existing = await _flights.GetTravelerFlightAsync(cmd.TravelerId, flight.Id, ct);
            if (existing is null)
            {
                var tf = TravelerFlight.Create(cmd.TravelerId, flight.Id);
                await _flights.AddTravelerFlightAsync(tf, ct);

                // First time anyone tracks it (or the first since the last untrack): plan it and make it
                // due now, so the sync picks it up within a loop (#356, spec §2.5).
                if (flight.SyncMode is null)
                {
                    var plan = FlightSyncPlanner.Plan(flight, isTracked: true, now,
                        FlightSyncPlanOptions.From(_syncOptions, _windowOptions));
                    flight.ApplySyncPlan(plan.Mode, plan.Mode == FlightSyncMode.Stopped ? null : now);
                }
            }

            // Raised on every track request, new or repeat (#410, R1): the airline or an airport may
            // have gone stale since the last time this flight was tracked. The save is unconditional so
            // the event dispatches even when nothing else changed -- the flight is EF-tracked either
            // way, so this save is cheap when there's nothing new to write.
            flight.MarkTracked(now);
            await _flights.SaveChangesAsync(ct);

            return flight.Id;
        }

        /// <summary>Whether the traveler already tracks this flight, found the way
        /// <c>GetOrCreateFlightAsync</c> would find its row.</summary>
        private async Task<bool> AlreadyTracksAsync(
            Guid travelerId, int airlineId, string flightNumber, DateOnly serviceDate, CancellationToken ct)
        {
            var flight = await _flights.GetByFlightNumberAndDateAsync(airlineId, flightNumber, serviceDate, ct);
            return flight is not null && await _flights.GetTravelerFlightAsync(travelerId, flight.Id, ct) is not null;
        }

        /// <summary>The one copy of the tracking-cap refusal, thrown from both the pre-provisioning
        /// check and the main one, so the two can never disagree about the wording.</summary>
        private static DomainException CapExceeded(int cap) =>
            new($"You can track up to {cap} flights at a time. Stop tracking one to add another.", 409);

        /// <summary>
        /// Turns a failed provisioning attempt into the right status. Only <c>NotFound</c> becomes a
        /// 404 -- a source answered and has no such entity. Everything else is a 503: we did not
        /// learn that the entity is absent, only that we could not find out.
        /// </summary>
        private static DomainException ToDomainException(ReferenceProvisionResult result) =>
            result.Status == ReferenceProvisionStatus.NotFound
                ? ReferenceNotFound(result.FailedType!.Value, result.FailedCode!)
                : new DomainException(
                    "Flight reference data could not be looked up right now. Please try again shortly.",
                    503,
                    result.DiagnosticDetail);

        /// <summary>The 404 messages are unchanged from before #465, so a client matching on them
        /// keeps working.</summary>
        private static DomainException ReferenceNotFound(VerificationEntityType type, string code) =>
            type switch
            {
                VerificationEntityType.Airline => new DomainException($"Airline '{code}' not found.", 404),
                VerificationEntityType.Airport => new DomainException($"Airport '{code}' not found.", 404),
                _ => new DomainException($"Country '{code}' not found.", 404)
            };
    }
}
