using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Validators;
using FlightAssistant.Contracts.Paging;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Airlines
{
    public record AirlineResult(
        int Id,
        string? Iata,
        string? Icao,
        string Name,
        string CountryCode,
        string? AllianceCode
    );

    public record GetAllAirlinesQuery(PagedRequest Paging) : IRequest<PagedResult<AirlineResult>>;

    public class GetAllAirlinesQueryValidator : AbstractValidator<GetAllAirlinesQuery>
    {
        public GetAllAirlinesQueryValidator()
        {
            RuleFor(x => x.Paging).SetValidator(new PagedRequestValidator());
        }
    }

    public sealed class GetAllAirlinesQueryHandler : IRequestHandler<GetAllAirlinesQuery, PagedResult<AirlineResult>>
    {
        private readonly IAirlineRepository _repo;
        public GetAllAirlinesQueryHandler(IAirlineRepository repo) => _repo = repo;

        public async Task<PagedResult<AirlineResult>> Handle(GetAllAirlinesQuery request, CancellationToken ct)
        {
            var (airlines, total) = await _repo.GetAllAsync(request.Paging.Page, request.Paging.PageSize, ct);
            var items = airlines.Select(a => new AirlineResult(a.Id, a.Iata, a.Icao, a.Name, a.CountryCode, a.AllianceCode)).ToList();
            return new PagedResult<AirlineResult>(items, total, request.Paging.Page, request.Paging.PageSize);
        }
    }
}
