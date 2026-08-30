using System.Text.RegularExpressions;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Validators;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Flights
{
    /// <summary>
    /// Searches for flights on a given <paramref name="Date"/>, anchored on either a full route
    /// (<paramref name="From"/> + <paramref name="To"/>) or a specific <paramref name="FlightNumber"/>.
    /// Airport codes are 3-letter IATA codes; the flight number is an IATA flight code (e.g. BA123).
    /// </summary>
    /// <param name="IncludeCodeshares">When <c>true</c>, codeshare-marketed rows for the same
    /// physical departure are included alongside the operating flight's own row. Defaults to
    /// <c>false</c>.</param>
    public record SearchFlightsQuery(
        string? From,
        string? To,
        DateOnly Date,
        string? FlightNumber = null,
        bool IncludeCodeshares = false)
        : IRequest<IReadOnlyList<FlightSearchResult>>;

    public class SearchFlightsQueryValidator : AbstractValidator<SearchFlightsQuery>
    {
        public SearchFlightsQueryValidator(IFlightSearchHorizonOptions horizon)
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
                // Shared with TrackFlightCommandValidator — a flight number that search accepts
                // must be one POST /flights accepts too, or the picker hands the client a row it
                // cannot track. See FlightNumberFormat.
                RuleFor(x => x.FlightNumber!)
                    .Matches(FlightNumberFormat.Pattern, RegexOptions.IgnoreCase)
                    .WithMessage($"'flightNumber' must be an IATA flight code: {FlightNumberFormat.Description}.");
            });

            // The provider's schedules endpoint only holds a rolling window of past and future
            // data (AeroApiOptions.MaxPastMonths / MaxFutureMonths). Rejecting a date outside it
            // here turns what would otherwise be an upstream 502 into a clear 400.
            //
            // The day of slack at each end is load-bearing, not padding: the provider queries a
            // window around the requested date, not the date itself — [D−1, D+2) when no origin
            // zone is available, and a local-day window that can start the day before or end the
            // day after in UTC. A date sitting exactly on the horizon would therefore produce a
            // date_start/date_end just outside the provider's own bounds and earn the upstream 400
            // this rule exists to pre-empt.
            RuleFor(x => x.Date)
                .Must(d => d >= DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-horizon.MaxPastMonths).AddDays(1)))
                .WithMessage($"Date must not be more than {horizon.MaxPastMonths} months in the past.");

            RuleFor(x => x.Date)
                .Must(d => d <= DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(horizon.MaxFutureMonths).AddDays(-1)))
                .WithMessage($"Date must not be more than {horizon.MaxFutureMonths} months in the future.");
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
                    FlightNumber: query.FlightNumber?.ToUpperInvariant(),
                    IncludeCodeshares: query.IncludeCodeshares),
                ct);
    }
}
