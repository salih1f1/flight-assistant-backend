using System.Text.RegularExpressions;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Flights
{
    /// <summary>
    /// Searches for flights on a given <paramref name="Date"/>, anchored on either a full route
    /// (<paramref name="From"/> + <paramref name="To"/>) or a specific <paramref name="FlightNumber"/>.
    /// Airport codes are 3-letter IATA codes; the flight number is an IATA flight code (e.g. BA123).
    /// </summary>
    public record SearchFlightsQuery(string? From, string? To, DateOnly Date, string? FlightNumber = null)
        : IRequest<IReadOnlyList<FlightSearchResult>>;

    public class SearchFlightsQueryValidator : AbstractValidator<SearchFlightsQuery>
    {
        // IATA flight code: 2-char airline prefix (alphanumeric, e.g. "BA", "U2") + 1-4 digit number.
        private const string FlightNumberPattern = "^[A-Z0-9]{2}[0-9]{1,4}$";

        // AviationStack's schedules endpoint rejects dates past a rolling horizon (observed as
        // ~11.5 months out). Stay just inside it so the window's drift never turns into a 502.
        private const int MaxFutureMonths = 11;

        public SearchFlightsQueryValidator()
        {
            // A search must be anchored on a full route OR a flight number — otherwise the result
            // set would be unbounded.
            RuleFor(x => x)
                .Must(HasRouteOrFlightNumber)
                .WithName("query")
                .WithMessage("Provide either both 'from' and 'to', or a 'flightNumber'.");

            When(x => !string.IsNullOrWhiteSpace(x.From), () =>
            {
                RuleFor(x => x.From!)
                    .Matches("^[A-Z]{3}$", RegexOptions.IgnoreCase)
                    .WithMessage("'from' must be a 3-letter IATA airport code.");
            });

            When(x => !string.IsNullOrWhiteSpace(x.To), () =>
            {
                RuleFor(x => x.To!)
                    .Matches("^[A-Z]{3}$", RegexOptions.IgnoreCase)
                    .WithMessage("'to' must be a 3-letter IATA airport code.");
            });

            // 'from' and 'to' are only meaningful as a pair — reject a half-specified route.
            When(x => !string.IsNullOrWhiteSpace(x.From) ^ !string.IsNullOrWhiteSpace(x.To), () =>
            {
                RuleFor(x => x)
                    .Must(_ => false)
                    .WithName("query")
                    .WithMessage("'from' and 'to' must be supplied together.");
            });

            When(x => !string.IsNullOrWhiteSpace(x.FlightNumber), () =>
            {
                RuleFor(x => x.FlightNumber!)
                    .Matches(FlightNumberPattern, RegexOptions.IgnoreCase)
                    .WithMessage("'flightNumber' must be an IATA flight code, e.g. 'BA123'.");
            });

            RuleFor(x => x.Date)
                .Must(d => d >= DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)))
                .WithMessage("Date must not be more than 1 year in the past.");

            // The provider's schedules endpoint refuses dates beyond a rolling horizon roughly a
            // year out. Rejecting them here turns an upstream 502 into a clear 400.
            RuleFor(x => x.Date)
                .Must(d => d <= DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(MaxFutureMonths)))
                .WithMessage($"Date must not be more than {MaxFutureMonths} months in the future.");
        }

        private static bool HasRouteOrFlightNumber(SearchFlightsQuery q) =>
            (!string.IsNullOrWhiteSpace(q.From) && !string.IsNullOrWhiteSpace(q.To))
            || !string.IsNullOrWhiteSpace(q.FlightNumber);
    }

    public sealed class SearchFlightsQueryHandler : IRequestHandler<SearchFlightsQuery, IReadOnlyList<FlightSearchResult>>
    {
        private readonly IFlightSearchProvider _search;

        public SearchFlightsQueryHandler(IFlightSearchProvider search) => _search = search;

        public Task<IReadOnlyList<FlightSearchResult>> Handle(SearchFlightsQuery query, CancellationToken ct) =>
            _search.SearchAsync(
                new FlightSearchCriteria(
                    DepartureIata: query.From?.ToUpperInvariant(),
                    ArrivalIata: query.To?.ToUpperInvariant(),
                    Date: query.Date,
                    FlightNumber: query.FlightNumber?.ToUpperInvariant()),
                ct);
    }
}
