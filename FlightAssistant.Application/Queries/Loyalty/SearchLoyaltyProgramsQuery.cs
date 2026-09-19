using FlightAssistant.Application.Interfaces.Repositories;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Loyalty
{
    public record SearchLoyaltyProgramsQuery(string AirlineIata) : IRequest<IReadOnlyList<LoyaltyProgramResult>>;

    public class SearchLoyaltyProgramsQueryValidator : AbstractValidator<SearchLoyaltyProgramsQuery>
    {
        public SearchLoyaltyProgramsQueryValidator()
        {
            RuleFor(x => x.AirlineIata)
                .NotEmpty()
                .Matches("^[A-Za-z0-9]{2}$").WithMessage("'airlineIata' must be a 2-character IATA airline code.");
        }
    }

    public sealed class SearchLoyaltyProgramsQueryHandler : IRequestHandler<SearchLoyaltyProgramsQuery, IReadOnlyList<LoyaltyProgramResult>>
    {
        private readonly ILoyaltyRepository _loyalty;
        private readonly IRefDataRepository _refData;

        public SearchLoyaltyProgramsQueryHandler(ILoyaltyRepository loyalty, IRefDataRepository refData)
        {
            _loyalty = loyalty;
            _refData = refData;
        }

        public async Task<IReadOnlyList<LoyaltyProgramResult>> Handle(SearchLoyaltyProgramsQuery query, CancellationToken ct)
        {
            var airlineId = await _refData.GetAirlineIdByIataAsync(query.AirlineIata.ToUpperInvariant(), ct);
            if (airlineId is null) return [];

            var programs = await _loyalty.SearchProgramsByAirlineIdAsync(airlineId.Value, ct);

            return programs.Select(p => new LoyaltyProgramResult(
                p.Id, p.Code, p.Name, p.Kind.Code, p.AirlineId,
                p.Tiers.Select(t => new LoyaltyTierResult(t.Id, t.Code, t.Name, t.Rank, t.AllianceCode, t.AllianceTierCode)).ToList()
            )).ToList();
        }
    }
}
