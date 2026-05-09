using FlightAssistant.Application.Commands.Admin.Airlines;
using FluentValidation;

namespace FlightAssistant.Application.Validators.Admin
{
    public sealed class CreateAirlineCommandValidator : AbstractValidator<CreateAirlineCommand>
    {
        public CreateAirlineCommandValidator()
        {
            RuleFor(x => x.Iata)
                .MaximumLength(3)
                .Matches(@"^[A-Z0-9]{2,3}$").WithMessage("IATA must be 2–3 uppercase letters or digits.")
                .When(x => x.Iata is not null);
            RuleFor(x => x.Icao)
                .MaximumLength(4)
                .Matches(@"^[A-Z]{3,4}$").WithMessage("ICAO must be 3–4 uppercase letters.")
                .When(x => x.Icao is not null);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.CountryCode)
                .NotEmpty().MaximumLength(2)
                .Matches(@"^[A-Z]{2}$").WithMessage("Country code must be ISO 3166-1 alpha-2.");
            RuleFor(x => x.AllianceCode).MaximumLength(8).When(x => x.AllianceCode is not null);
        }
    }

    public sealed class UpdateAirlineCommandValidator : AbstractValidator<UpdateAirlineCommand>
    {
        public UpdateAirlineCommandValidator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
            RuleFor(x => x.Iata)
                .MaximumLength(3)
                .Matches(@"^[A-Z0-9]{2,3}$").WithMessage("IATA must be 2–3 uppercase letters or digits.")
                .When(x => x.Iata is not null);
            RuleFor(x => x.Icao)
                .MaximumLength(4)
                .Matches(@"^[A-Z]{3,4}$").WithMessage("ICAO must be 3–4 uppercase letters.")
                .When(x => x.Icao is not null);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.CountryCode)
                .NotEmpty().MaximumLength(2)
                .Matches(@"^[A-Z]{2}$").WithMessage("Country code must be ISO 3166-1 alpha-2.");
            RuleFor(x => x.AllianceCode).MaximumLength(8).When(x => x.AllianceCode is not null);
        }
    }
}
