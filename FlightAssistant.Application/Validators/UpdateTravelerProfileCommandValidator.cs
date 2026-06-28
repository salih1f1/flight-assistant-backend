using FlightAssistant.Application.Commands.Travelers;
using FluentValidation;

namespace FlightAssistant.Application.Validators
{
    public sealed class UpdateTravelerProfileCommandValidator : AbstractValidator<UpdateTravelerProfileCommand>
    {
        private static readonly Func<string?, bool> IsValidAlpha2 =
            s => s is null || System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Z]{2}$");

        public UpdateTravelerProfileCommandValidator()
        {
            RuleFor(x => x.NationalityCountryCode)
                .Must(IsValidAlpha2)
                .WithMessage("NationalityCountryCode must be an ISO 3166-1 alpha-2 code.");

            RuleFor(x => x.ResidenceCountryCode)
                .Must(IsValidAlpha2)
                .WithMessage("ResidenceCountryCode must be an ISO 3166-1 alpha-2 code.");
        }
    }
}
