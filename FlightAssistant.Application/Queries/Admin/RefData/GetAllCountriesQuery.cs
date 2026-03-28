using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.RefData
{
    public record CountryResult(
        string Code,
        string Name,
        string? Alpha3,
        int? NumericCode,
        string? Region,
        string? Subregion,
        bool? IsEU,
        bool? IsSchengen,
        bool? IsEEA
    );

    public record GetAllCountriesQuery : IRequest<IReadOnlyList<CountryResult>>;

    public sealed class GetAllCountriesQueryHandler : IRequestHandler<GetAllCountriesQuery, IReadOnlyList<CountryResult>>
    {
        private readonly ICountryRepository _repo;
        public GetAllCountriesQueryHandler(ICountryRepository repo) => _repo = repo;

        public async Task<IReadOnlyList<CountryResult>> Handle(GetAllCountriesQuery request, CancellationToken ct)
        {
            var countries = await _repo.GetAllAsync(ct);
            return countries.Select(c => new CountryResult(
                c.Code, c.Name, c.Alpha3, c.NumericCode, c.Region, c.Subregion,
                c.IsEU, c.IsSchengen, c.IsEEA
            )).ToList();
        }
    }
}
