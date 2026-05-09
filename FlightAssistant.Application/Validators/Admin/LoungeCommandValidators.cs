using FlightAssistant.Application.Commands.Admin.Lounges;
using FluentValidation;

namespace FlightAssistant.Application.Validators.Admin
{
    public sealed class CreateLoungeCommandValidator : AbstractValidator<CreateLoungeCommand>
    {
        public CreateLoungeCommandValidator()
        {
            RuleFor(x => x.AirportId).GreaterThan(0);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Terminal).MaximumLength(32).When(x => x.Terminal is not null);
            RuleFor(x => x.LocationHint).MaximumLength(200).When(x => x.LocationHint is not null);
            RuleFor(x => x.Hours).MaximumLength(200).When(x => x.Hours is not null);
            RuleFor(x => x.Amenities).MaximumLength(1000).When(x => x.Amenities is not null);
        }
    }

    public sealed class UpdateLoungeCommandValidator : AbstractValidator<UpdateLoungeCommand>
    {
        public UpdateLoungeCommandValidator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Terminal).MaximumLength(32).When(x => x.Terminal is not null);
            RuleFor(x => x.LocationHint).MaximumLength(200).When(x => x.LocationHint is not null);
            RuleFor(x => x.Hours).MaximumLength(200).When(x => x.Hours is not null);
            RuleFor(x => x.Amenities).MaximumLength(1000).When(x => x.Amenities is not null);
        }
    }
}
