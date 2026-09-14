using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Validators;
using FlightAssistant.Contracts.Paging;
using FluentValidation;
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
        string? EquipmentAircraftIcao
    );

    public record GetAllFlightsAdminQuery(PagedRequest Paging) : IRequest<PagedResult<AdminFlightResult>>;

    public class GetAllFlightsAdminQueryValidator : AbstractValidator<GetAllFlightsAdminQuery>
    {
        public GetAllFlightsAdminQueryValidator()
        {
            RuleFor(x => x.Paging).SetValidator(new PagedRequestValidator());
        }
    }

    public sealed class GetAllFlightsAdminQueryHandler : IRequestHandler<GetAllFlightsAdminQuery, PagedResult<AdminFlightResult>>
    {
        private readonly IAdminFlightRepository _repo;
        public GetAllFlightsAdminQueryHandler(IAdminFlightRepository repo) => _repo = repo;

        public async Task<PagedResult<AdminFlightResult>> Handle(GetAllFlightsAdminQuery request, CancellationToken ct)
        {
            var (flights, total) = await _repo.GetAllAsync(request.Paging.Page, request.Paging.PageSize, ct);
            var items = flights.Select(f => new AdminFlightResult(
                f.Id, f.AirlineId, f.FlightNumber, f.DepartureAirportId, f.ArrivalAirportId,
                f.ServiceDateUtc, f.ScheduledDepartureUtc, f.ScheduledArrivalUtc,
                f.EstimatedDepartureUtc, f.EstimatedArrivalUtc,
                f.ActualDepartureUtc, f.ActualArrivalUtc,
                f.EquipmentAircraftIcao
            )).ToList();
            return new PagedResult<AdminFlightResult>(items, total, request.Paging.Page, request.Paging.PageSize);
        }
    }
}
