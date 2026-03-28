using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Flights
{
    public record AdminFlightResult(
        int Id,
        int AirlineId,
        string FlightNumber,
        int DepartureAirportId,
        int ArrivalAirportId,
        DateOnly ServiceDate,
        DateTime ScheduledDepartureUtc,
        DateTime ScheduledArrivalUtc,
        DateTime? EstimatedDepartureUtc,
        DateTime? EstimatedArrivalUtc,
        DateTime? ActualDepartureUtc,
        DateTime? ActualArrivalUtc,
        string? EquipmentAircraftIcao,
        string? ExternalId
    );

    public record GetAllFlightsAdminQuery : IRequest<IReadOnlyList<AdminFlightResult>>;

    public sealed class GetAllFlightsAdminQueryHandler : IRequestHandler<GetAllFlightsAdminQuery, IReadOnlyList<AdminFlightResult>>
    {
        private readonly IAdminFlightRepository _repo;
        public GetAllFlightsAdminQueryHandler(IAdminFlightRepository repo) => _repo = repo;

        public async Task<IReadOnlyList<AdminFlightResult>> Handle(GetAllFlightsAdminQuery request, CancellationToken ct)
        {
            var flights = await _repo.GetAllAsync(ct);
            return flights.Select(f => new AdminFlightResult(
                f.Id, f.AirlineId, f.FlightNumber, f.DepartureAirportId, f.ArrivalAirportId,
                f.ServiceDateUtc, f.ScheduledDepartureUtc, f.ScheduledArrivalUtc,
                f.EstimatedDepartureUtc, f.EstimatedArrivalUtc,
                f.ActualDepartureUtc, f.ActualArrivalUtc,
                f.EquipmentAircraftIcao, f.ExternalId
            )).ToList();
        }
    }
}
