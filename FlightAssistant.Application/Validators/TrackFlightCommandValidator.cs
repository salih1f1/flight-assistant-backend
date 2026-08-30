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
                // A real IATA flight designator's 2-character airline prefix always contains at
                // least one letter (e.g. "6E", "U2", "9W", "B6") — an all-digit prefix like "12"
                // is not a carrier. [A-Z][A-Z0-9] covers letter-first prefixes (including
                // letter+digit and letter+letter); [0-9][A-Z] covers digit-first prefixes. Whether
                // a given shape is an actually-assigned carrier is reference data's job — see
                // TrackFlightCommandHandler's 404 — not this regex's.
                .Matches(@"^(?:[A-Z][A-Z0-9]|[0-9][A-Z])\d{1,4}[A-Z]?$", RegexOptions.IgnoreCase)
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
