using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Flights
{
    public record TravelerFlightResult(
        int FlightId,
        string FlightNumber,
        int AirlineId,
        int DepartureAirportId,
        int ArrivalAirportId,
        DateOnly ServiceDate,
        DateTime ScheduledDepartureUtc,
        DateTime ScheduledArrivalUtc,
        DateTime? EstimatedDepartureUtc,
        DateTime? EstimatedArrivalUtc,
        DateTime? ActualDepartureUtc,
        DateTime? ActualArrivalUtc,
        bool Notify
    );

    public record GetTravelerFlightsQuery(Guid TravelerId) : IRequest<IReadOnlyList<TravelerFlightResult>>;

    public sealed class GetTravelerFlightsQueryHandler : IRequestHandler<GetTravelerFlightsQuery, IReadOnlyList<TravelerFlightResult>>
    {
        private readonly IFlightRepository _flights;

        public GetTravelerFlightsQueryHandler(IFlightRepository flights) => _flights = flights;

        public async Task<IReadOnlyList<TravelerFlightResult>> Handle(GetTravelerFlightsQuery query, CancellationToken ct)
        {
            var rows = await _flights.GetTravelerFlightsAsync(query.TravelerId, ct);
            return rows.Select(r => new TravelerFlightResult(
                r.Flight.Id,
                r.Flight.FlightNumber,
                r.Flight.AirlineId,
                r.Flight.DepartureAirportId,
                r.Flight.ArrivalAirportId,
                r.Flight.ServiceDateUtc,
                r.Flight.ScheduledDepartureUtc,
                r.Flight.ScheduledArrivalUtc,
                r.Flight.EstimatedDepartureUtc,
                r.Flight.EstimatedArrivalUtc,
                r.Flight.ActualDepartureUtc,
                r.Flight.ActualArrivalUtc,
                r.Tracking.Notify
            )).ToList();
        }
    }
}
