using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Validators;
using FlightAssistant.Contracts.Paging;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Flights
{
    public record TravelerFlightResult(
        int FlightId,
        string FlightNumber,
        string AirlineIata,
        string DepartureIata,
        string ArrivalIata,
        DateOnly ServiceDate,
        DateTime ScheduledDepartureUtc,
        DateTime ScheduledArrivalUtc,
        DateTime? EstimatedDepartureUtc,
        DateTime? EstimatedArrivalUtc,
        DateTime? ActualDepartureUtc,
        DateTime? ActualArrivalUtc,
        bool Notify
    );

    public record GetTravelerFlightsQuery(Guid TravelerId, PagedRequest Paging) : IRequest<PagedResult<TravelerFlightResult>>;

    public class GetTravelerFlightsQueryValidator : AbstractValidator<GetTravelerFlightsQuery>
    {
        public GetTravelerFlightsQueryValidator()
        {
            RuleFor(x => x.Paging).SetValidator(new PagedRequestValidator());
        }
    }

    public sealed class GetTravelerFlightsQueryHandler : IRequestHandler<GetTravelerFlightsQuery, PagedResult<TravelerFlightResult>>
    {
        private readonly IFlightRepository _flights;
        private readonly IRefDataRepository _refData;

        public GetTravelerFlightsQueryHandler(IFlightRepository flights, IRefDataRepository refData)
        {
            _flights = flights;
            _refData = refData;
        }

        public async Task<PagedResult<TravelerFlightResult>> Handle(GetTravelerFlightsQuery query, CancellationToken ct)
        {
            var (rows, total) = await _flights.GetTravelerFlightsAsync(query.TravelerId, query.Paging.Page, query.Paging.PageSize, ct);

            var results = new List<TravelerFlightResult>(rows.Count);
            foreach (var r in rows)
            {
                var airlineIata = await _refData.GetAirlineIataByIdAsync(r.Flight.AirlineId, ct) ?? r.Flight.AirlineId.ToString();
                var depIata    = await _refData.GetAirportIataByIdAsync(r.Flight.DepartureAirportId, ct) ?? r.Flight.DepartureAirportId.ToString();
                var arrIata    = await _refData.GetAirportIataByIdAsync(r.Flight.ArrivalAirportId, ct) ?? r.Flight.ArrivalAirportId.ToString();

                results.Add(new TravelerFlightResult(
                    r.Flight.Id,
                    r.Flight.FlightNumber,
                    airlineIata,
                    depIata,
                    arrIata,
                    r.Flight.ServiceDateUtc,
                    r.Flight.ScheduledDepartureUtc,
                    r.Flight.ScheduledArrivalUtc,
                    r.Flight.EstimatedDepartureUtc,
                    r.Flight.EstimatedArrivalUtc,
                    r.Flight.ActualDepartureUtc,
                    r.Flight.ActualArrivalUtc,
                    r.Tracking.Notify
                ));
            }
            return new PagedResult<TravelerFlightResult>(results, total, query.Paging.Page, query.Paging.PageSize);
        }
    }
}
