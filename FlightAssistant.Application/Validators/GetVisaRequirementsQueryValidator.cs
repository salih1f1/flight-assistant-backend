using FlightAssistant.Application.Queries.Visa;
using FluentValidation;

namespace FlightAssistant.Application.Validators
{
    public sealed class GetVisaRequirementsQueryValidator : AbstractValidator<GetVisaRequirementsQuery>
    {
        public GetVisaRequirementsQueryValidator()
        {
            RuleFor(x => x.PassportCountryCode)
                .NotEmpty()
                .Matches(@"^[A-Z]{2}$")
                .WithMessage("PassportCountryCode must be an ISO 3166-1 alpha-2 code (e.g. GB).");

            RuleFor(x => x.DestinationCountryCode)
                .NotEmpty()
                .Matches(@"^[A-Z]{2}$")
                .WithMessage("DestinationCountryCode must be an ISO 3166-1 alpha-2 code (e.g. US).");
        }
    }
}
