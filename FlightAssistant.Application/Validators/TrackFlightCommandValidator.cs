using System.Text.RegularExpressions;
using FlightAssistant.Application.Commands.Flights;
using FluentValidation;

namespace FlightAssistant.Application.Validators
{
    public sealed class TrackFlightCommandValidator : AbstractValidator<TrackFlightCommand>
    {
        public TrackFlightCommandValidator()
        {
            RuleFor(x => x.FlightNumber)
                .NotEmpty()
                .Matches(@"^[A-Z0-9]{2}\d{1,4}[A-Z]?$", RegexOptions.IgnoreCase)
                .WithMessage("FlightNumber must be in the format AA1234 or AA1234A.");

            RuleFor(x => x.ServiceDate)
                .Must(d => d >= DateOnly.FromDateTime(DateTime.UtcNow.Date))
                .WithMessage("ServiceDate cannot be in the past.");

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
        }
    }
}
