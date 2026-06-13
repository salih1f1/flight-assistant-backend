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

            var airlineIds = rows.Select(r => r.Flight.AirlineId).Distinct().ToList();
            var airportIds = rows
                .SelectMany(r => new[] { r.Flight.DepartureAirportId, r.Flight.ArrivalAirportId })
                .Distinct().ToList();

            var airlineMap = await _refData.GetAirlineIataMapAsync(airlineIds, ct);
            var airportMap = await _refData.GetAirportIataMapAsync(airportIds, ct);

            var results = new List<TravelerFlightResult>(rows.Count);
            foreach (var r in rows)
            {
                var airlineIata = airlineMap.GetValueOrDefault(r.Flight.AirlineId, r.Flight.AirlineId.ToString());
                var depIata     = airportMap.GetValueOrDefault(r.Flight.DepartureAirportId, r.Flight.DepartureAirportId.ToString());
                var arrIata     = airportMap.GetValueOrDefault(r.Flight.ArrivalAirportId, r.Flight.ArrivalAirportId.ToString());

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
