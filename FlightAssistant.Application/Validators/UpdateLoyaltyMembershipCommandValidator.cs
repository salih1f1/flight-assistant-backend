using FlightAssistant.Application.Commands.Loyalty;
using FluentValidation;

namespace FlightAssistant.Application.Validators
{
    public sealed class UpdateLoyaltyMembershipCommandValidator : AbstractValidator<UpdateLoyaltyMembershipCommand>
    {
        public UpdateLoyaltyMembershipCommandValidator()
        {
            // Optional: null keeps the current account number (#548). A value that is sent must
            // still be a real one — a blank string is not "keep".
            RuleFor(x => x.AccountNumber)
                .NotEmpty()
                .MaximumLength(64)
                .When(x => x.AccountNumber is not null);

            RuleFor(x => x.TierExpiryDate)
                .GreaterThanOrEqualTo(DateOnly.FromDateTime(DateTime.UtcNow))
                .WithMessage("Tier expiry date must be today or in the future.")
                .When(x => x.TierExpiryDate.HasValue);
        }
    }
}
