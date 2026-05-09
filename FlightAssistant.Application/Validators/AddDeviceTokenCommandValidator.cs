using FlightAssistant.Application.Commands.Travelers;
using FluentValidation;

namespace FlightAssistant.Application.Validators
{
    public sealed class AddDeviceTokenCommandValidator : AbstractValidator<AddDeviceTokenCommand>
    {
        // Supported push platforms. Extend when Android/Web push is added.
        private static readonly HashSet<string> SupportedPlatforms =
            new(StringComparer.OrdinalIgnoreCase) { "ios" };

        public AddDeviceTokenCommandValidator()
        {
            RuleFor(x => x.Token)
                .NotEmpty()
                .MaximumLength(2048);

            RuleFor(x => x.Platform)
                .NotEmpty()
                .Must(p => SupportedPlatforms.Contains(p))
                .WithMessage($"Platform must be one of: {string.Join(", ", SupportedPlatforms)}.");
        }
    }
}
