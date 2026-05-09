using FlightAssistant.Application.Commands.Loyalty;
using FluentValidation;

namespace FlightAssistant.Application.Validators
{
    public sealed class UpdateLoyaltyMembershipCommandValidator : AbstractValidator<UpdateLoyaltyMembershipCommand>
    {
        public UpdateLoyaltyMembershipCommandValidator()
        {
            RuleFor(x => x.AccountNumber)
                .NotEmpty()
                .MaximumLength(64);
        }
    }
}
