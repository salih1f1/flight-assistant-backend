using FlightAssistant.Application.Commands.Admin.Airports;
using FluentValidation;

namespace FlightAssistant.Application.Validators.Admin
{
    public sealed class CreateAirportCommandValidator : AbstractValidator<CreateAirportCommand>
    {
        public CreateAirportCommandValidator()
        {
            RuleFor(x => x.Iata)
                .NotEmpty().MaximumLength(3)
                .Matches(@"^[A-Z]{3}$").WithMessage("IATA must be exactly 3 uppercase letters.");
            RuleFor(x => x.Icao)
                .MaximumLength(4)
                .Matches(@"^[A-Z]{4}$").WithMessage("ICAO must be exactly 4 uppercase letters.")
                .When(x => x.Icao is not null);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.City).NotEmpty().MaximumLength(200);
            RuleFor(x => x.CountryCode)
                .NotEmpty().MaximumLength(2)
                .Matches(@"^[A-Z]{2}$").WithMessage("Country code must be ISO 3166-1 alpha-2.");
            RuleFor(x => x.Timezone).MaximumLength(64).When(x => x.Timezone is not null);
        }
    }

    public sealed class UpdateAirportCommandValidator : AbstractValidator<UpdateAirportCommand>
    {
        public UpdateAirportCommandValidator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
            RuleFor(x => x.Iata)
                .NotEmpty().MaximumLength(3)
                .Matches(@"^[A-Z]{3}$").WithMessage("IATA must be exactly 3 uppercase letters.");
            RuleFor(x => x.Icao)
                .MaximumLength(4)
                .Matches(@"^[A-Z]{4}$").WithMessage("ICAO must be exactly 4 uppercase letters.")
                .When(x => x.Icao is not null);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.City).NotEmpty().MaximumLength(200);
            RuleFor(x => x.CountryCode)
                .NotEmpty().MaximumLength(2)
                .Matches(@"^[A-Z]{2}$").WithMessage("Country code must be ISO 3166-1 alpha-2.");
            RuleFor(x => x.Timezone).MaximumLength(64).When(x => x.Timezone is not null);
        }
    }
}
