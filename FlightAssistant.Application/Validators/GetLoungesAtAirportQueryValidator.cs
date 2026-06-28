using System.Text.RegularExpressions;
using FlightAssistant.Application.Queries.Lounges;
using FluentValidation;

namespace FlightAssistant.Application.Validators
{
    public sealed class GetLoungesAtAirportQueryValidator : AbstractValidator<GetLoungesAtAirportQuery>
    {
        public GetLoungesAtAirportQueryValidator()
        {
            RuleFor(x => x.AirportIata)
                .NotEmpty()
                .Matches(@"^[A-Z]{3}$", RegexOptions.IgnoreCase)
                .WithMessage("AirportIata must be a 3-letter IATA code (e.g. LHR).");
        }
    }
}
