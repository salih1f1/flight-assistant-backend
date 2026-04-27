using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Validators;
using FlightAssistant.Contracts.Paging;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Airports
{
    public record AirportResult(
        int Id,
        string Iata,
        string? Icao,
        string Name,
        string City,
        string CountryCode,
        double? Latitude,
        double? Longitude,
        int? ElevationFt,
        string? Timezone
    );

    public record GetAllAirportsQuery(PagedRequest Paging) : IRequest<PagedResult<AirportResult>>;

    public sealed class GetAllAirportsQueryValidator : AbstractValidator<GetAllAirportsQuery>
    {
        public GetAllAirportsQueryValidator()
        {
            RuleFor(x => x.Paging).SetValidator(new PagedRequestValidator());
        }
    }

    public sealed class GetAllAirportsQueryHandler : IRequestHandler<GetAllAirportsQuery, PagedResult<AirportResult>>
    {
        private readonly IAirportRepository _repo;
        public GetAllAirportsQueryHandler(IAirportRepository repo) => _repo = repo;

        public async Task<PagedResult<AirportResult>> Handle(GetAllAirportsQuery request, CancellationToken ct)
        {
            var (airports, total) = await _repo.GetAllAsync(request.Paging.Page, request.Paging.PageSize, ct);
            var items = airports.Select(a => new AirportResult(
                a.Id, a.Iata, a.Icao, a.Name, a.City, a.CountryCode,
                a.Latitude, a.Longitude, a.ElevationFt, a.Timezone
            )).ToList();
            return new PagedResult<AirportResult>(items, total, request.Paging.Page, request.Paging.PageSize);
        }
    }
}
