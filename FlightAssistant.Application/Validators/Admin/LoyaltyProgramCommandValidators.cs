using FlightAssistant.Application.Commands.Admin.Loyalty;
using FluentValidation;

namespace FlightAssistant.Application.Validators.Admin
{
    public sealed class CreateLoyaltyProgramCommandValidator : AbstractValidator<CreateLoyaltyProgramCommand>
    {
        public CreateLoyaltyProgramCommandValidator()
        {
            RuleFor(x => x.Code).NotEmpty().MaximumLength(16);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.KindId).GreaterThan(0);
            RuleFor(x => x.AllianceCode).MaximumLength(8).When(x => x.AllianceCode is not null);
        }
    }

    public sealed class UpdateLoyaltyProgramCommandValidator : AbstractValidator<UpdateLoyaltyProgramCommand>
    {
        public UpdateLoyaltyProgramCommandValidator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
            RuleFor(x => x.Code).NotEmpty().MaximumLength(16);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.KindId).GreaterThan(0);
            RuleFor(x => x.AllianceCode).MaximumLength(8).When(x => x.AllianceCode is not null);
        }
    }
}
