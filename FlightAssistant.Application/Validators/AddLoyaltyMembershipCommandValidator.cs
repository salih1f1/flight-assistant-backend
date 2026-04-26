using FlightAssistant.Application.Commands.Loyalty;
using FluentValidation;

namespace FlightAssistant.Application.Validators
{
    public sealed class AddLoyaltyMembershipCommandValidator : AbstractValidator<AddLoyaltyMembershipCommand>
    {
        public AddLoyaltyMembershipCommandValidator()
        {
            RuleFor(x => x.ProgramId)
                .GreaterThan(0);

            RuleFor(x => x.AccountNumber)
                .NotEmpty()
                .MaximumLength(100);
        }
    }
}
