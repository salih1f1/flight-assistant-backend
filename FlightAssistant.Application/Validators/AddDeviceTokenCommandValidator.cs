using FlightAssistant.Application.Commands.Travelers;
using FluentValidation;

namespace FlightAssistant.Application.Validators
{
    public sealed class AddDeviceTokenCommandValidator : AbstractValidator<AddDeviceTokenCommand>
    {
        public AddDeviceTokenCommandValidator()
        {
            RuleFor(x => x.Token)
                .NotEmpty()
                .MaximumLength(200);

            RuleFor(x => x.Platform)
                .NotEmpty()
                .MaximumLength(20);
        }
    }
}
