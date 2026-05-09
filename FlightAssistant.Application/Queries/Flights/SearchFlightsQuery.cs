using FlightAssistant.Application.Interfaces.ExternalServices;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Flights
{
    public record SearchFlightsQuery(string From, string To, DateOnly Date)
        : IRequest<IReadOnlyList<FlightSearchResult>>;

    public class SearchFlightsQueryValidator : AbstractValidator<SearchFlightsQuery>
    {
        public SearchFlightsQueryValidator()
        {
            RuleFor(x => x.From)
                .NotEmpty()
                .Length(3)
                .Matches("^[A-Z]{3}$").WithMessage("'From' must be a 3-letter uppercase IATA airport code.");

            RuleFor(x => x.To)
                .NotEmpty()
                .Length(3)
                .Matches("^[A-Z]{3}$").WithMessage("'To' must be a 3-letter uppercase IATA airport code.");

            RuleFor(x => x.Date)
                .Must(d => d >= DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)))
                .WithMessage("Date must not be more than 1 year in the past.");
        }
    }

    public sealed class SearchFlightsQueryHandler : IRequestHandler<SearchFlightsQuery, IReadOnlyList<FlightSearchResult>>
    {
        private readonly IFlightSearchProvider _search;

        public SearchFlightsQueryHandler(IFlightSearchProvider search) => _search = search;

        public Task<IReadOnlyList<FlightSearchResult>> Handle(SearchFlightsQuery query, CancellationToken ct) =>
            _search.SearchAsync(query.From.ToUpperInvariant(), query.To.ToUpperInvariant(), query.Date, ct);
    }
}
