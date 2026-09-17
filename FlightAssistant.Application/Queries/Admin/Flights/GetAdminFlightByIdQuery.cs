using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Flights;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Flights
{
    public record GetAdminFlightByIdQuery(int Id) : IRequest<AdminFlightResult?>;

    public sealed class GetAdminFlightByIdQueryHandler : IRequestHandler<GetAdminFlightByIdQuery, AdminFlightResult?>
    {
        private readonly IAdminFlightRepository _repo;
        public GetAdminFlightByIdQueryHandler(IAdminFlightRepository repo) => _repo = repo;

        public async Task<AdminFlightResult?> Handle(GetAdminFlightByIdQuery request, CancellationToken ct)
        {
            var f = await _repo.GetByIntIdAsync(request.Id, ct);
            if (f is null) return null;
            return new AdminFlightResult(
                f.Id, f.AirlineId, f.FlightNumber, f.DepartureAirportId, f.ArrivalAirportId,
                f.ServiceDateUtc, f.ScheduledDepartureUtc, f.ScheduledArrivalUtc,
                f.EstimatedDepartureUtc, f.EstimatedArrivalUtc,
                f.ActualDepartureUtc, f.ActualArrivalUtc,
                f.EquipmentAircraftIcao,
                f.LastSyncedUtc, f.NextSyncDueUtc, f.SyncMode?.ToString(),
                f.ScheduleMissingSinceUtc, f.CancelledAtUtc
            );
        }
    }
}
