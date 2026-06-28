using FlightAssistant.Application.Commands.Travelers;
using FluentValidation;

namespace FlightAssistant.Application.Validators
{
    public sealed class RegisterTravelerCommandValidator : AbstractValidator<RegisterTravelerCommand>
    {
        public RegisterTravelerCommandValidator()
        {
            RuleFor(x => x.Email)
                .NotEmpty()
                .EmailAddress()
                .MaximumLength(254);

            RuleFor(x => x.DisplayName)
                .MaximumLength(100)
                .When(x => x.DisplayName is not null);
        }
    }
}
