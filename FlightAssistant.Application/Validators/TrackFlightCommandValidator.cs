using System.Text.RegularExpressions;
using FlightAssistant.Application.Commands.Flights;
using FluentValidation;

namespace FlightAssistant.Application.Validators
{
    public sealed class TrackFlightCommandValidator : AbstractValidator<TrackFlightCommand>
    {
        public TrackFlightCommandValidator(TimeProvider timeProvider)
        {
            RuleFor(x => x.FlightNumber)
                .NotEmpty()
                // Shared with SearchFlightsQueryValidator — see FlightNumberFormat for why this
                // must be one constant and not two copies of a regex.
                .Matches(FlightNumberFormat.Pattern, RegexOptions.IgnoreCase)
                .WithMessage("FlightNumber must be in the format AA1234 or AA1234A.");

            RuleFor(x => x.DepartureAirportIata)
                .NotEmpty()
                .Matches(@"^[A-Z]{3}$", RegexOptions.IgnoreCase)
                .WithMessage("DepartureAirportIata must be a 3-letter IATA code.");

            RuleFor(x => x.ArrivalAirportIata)
                .NotEmpty()
                .Matches(@"^[A-Z]{3}$", RegexOptions.IgnoreCase)
                .WithMessage("ArrivalAirportIata must be a 3-letter IATA code.");

            RuleFor(x => x.ScheduledDepartureUtc)
                .NotEmpty();

            RuleFor(x => x.ScheduledArrivalUtc)
                .NotEmpty()
                .GreaterThan(x => x.ScheduledDepartureUtc)
                .WithMessage("ScheduledArrivalUtc must be after ScheduledDepartureUtc.");

            // A flight is in the past once it has landed. ServiceDate is deliberately not checked:
            // it is the origin airport's local date, so comparing it with today's UTC date rejected
            // tonight's flight west of UTC once the UTC date had rolled over, and a flight still in
            // the air after its service date (#350).
            RuleFor(x => x.ScheduledArrivalUtc)
                .Must(arrival => arrival > timeProvider.GetUtcNow().UtcDateTime)
                .WithMessage("This flight has already landed: ScheduledArrivalUtc is in the past.");
        }
    }
}
