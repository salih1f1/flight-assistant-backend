using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Flights
{
    public record TrackFlightCommand(
        Guid TravelerId,
        string AirlineIata,
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

        public TrackFlightCommandHandler(IFlightRepository flights, IRefDataRepository refData)
        {
            _flights = flights;
            _refData = refData;
        }

        public async Task<int> Handle(TrackFlightCommand cmd, CancellationToken ct)
        {
            var airlineId = await _refData.GetAirlineIdByIataAsync(cmd.AirlineIata, ct)
                ?? throw new DomainException($"Airline '{cmd.AirlineIata}' not found.", 404);

            var depAirportId = await _refData.GetAirportIdByIataAsync(cmd.DepartureAirportIata, ct)
                ?? throw new DomainException($"Airport '{cmd.DepartureAirportIata}' not found.", 404);

            var arrAirportId = await _refData.GetAirportIdByIataAsync(cmd.ArrivalAirportIata, ct)
                ?? throw new DomainException($"Airport '{cmd.ArrivalAirportIata}' not found.", 404);

            // Look up or create the Flight aggregate
            var flightNumber = cmd.FlightNumber.Trim().ToUpperInvariant();
            var flight = await _flights.GetByFlightNumberAndDateAsync(airlineId, flightNumber, cmd.ServiceDate, ct);
            if (flight is null)
            {
                flight = Flight.Create(airlineId, flightNumber, depAirportId, arrAirportId, cmd.ServiceDate, cmd.ScheduledDepartureUtc, cmd.ScheduledArrivalUtc);
                await _flights.AddAsync(flight, ct);
                await _flights.SaveChangesAsync(ct);
            }

            // Idempotent: skip if traveler already tracking this flight
            var existing = await _flights.GetTravelerFlightAsync(cmd.TravelerId, flight.Id, ct);
            if (existing is null)
            {
                var tf = TravelerFlight.Create(cmd.TravelerId, flight.Id);
                await _flights.AddTravelerFlightAsync(tf, ct);
                await _flights.SaveChangesAsync(ct);
            }

            return flight.Id;
        }
    }
}
