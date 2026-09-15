using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
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
        private readonly IFlightSyncOptions _syncOptions;
        private readonly IFlightStatusWindowOptions _windowOptions;
        private readonly TimeProvider _timeProvider;

        public TrackFlightCommandHandler(
            IFlightRepository flights,
            IRefDataRepository refData,
            IFlightSyncOptions syncOptions,
            IFlightStatusWindowOptions windowOptions,
            TimeProvider timeProvider)
        {
            _flights = flights;
            _refData = refData;
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
            var airlineId = await _refData.GetAirlineIdByIataAsync(airlineIata, ct)
                ?? throw new DomainException($"Airline '{airlineIata}' not found.", 404);

            var depAirportId = await _refData.GetAirportIdByIataAsync(cmd.DepartureAirportIata, ct)
                ?? throw new DomainException($"Airport '{cmd.DepartureAirportIata}' not found.", 404);

            var arrAirportId = await _refData.GetAirportIdByIataAsync(cmd.ArrivalAirportIata, ct)
                ?? throw new DomainException($"Airport '{cmd.ArrivalAirportIata}' not found.", 404);

            var flightNumber = cmd.FlightNumber.Trim().ToUpperInvariant();
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // A cap on flights the traveler tracks that haven't finished (#380), since each becomes a Live
            // and then Fast poll. Checked before the flight row exists, so a refused request writes nothing.
            // A flight the traveler already tracks still succeeds, so tracking stays idempotent.
            var cap = _syncOptions.MaxActiveTrackedFlightsPerTraveler;
            if (await _flights.CountActiveTrackedFlightsAsync(cmd.TravelerId, now, ct) >= cap
                && !await AlreadyTracksAsync(cmd.TravelerId, airlineId, flightNumber, cmd.ServiceDate, ct))
            {
                throw new DomainException(
                    $"You can track up to {cap} flights at a time. Stop tracking one to add another.", 409);
            }

            // Look up or create the Flight aggregate (race-safe via the unique ix_flight_lookup index)
            var candidate = Flight.Create(airlineId, flightNumber, depAirportId, arrAirportId, cmd.ServiceDate, cmd.ScheduledDepartureUtc, cmd.ScheduledArrivalUtc);
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

                await _flights.SaveChangesAsync(ct);
            }

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
    }
}
