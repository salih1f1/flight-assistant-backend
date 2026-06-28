using System.Text.RegularExpressions;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Airlines
{
    public record CreateAirlineCommand(
        string? Iata,
        string? Icao,
        string Name,
        string CountryCode,
        string? AllianceCode
    ) : IRequest<int>;

    public sealed class CreateAirlineCommandHandler : IRequestHandler<CreateAirlineCommand, int>
    {
        private readonly IAirlineRepository _repo;
        public CreateAirlineCommandHandler(IAirlineRepository repo) => _repo = repo;

        public async Task<int> Handle(CreateAirlineCommand cmd, CancellationToken ct)
        {
            var airline = Airline.Create(cmd.Iata, cmd.Icao, cmd.Name, cmd.CountryCode, cmd.AllianceCode);
            await _repo.AddAsync(airline, ct);
            await _repo.SaveChangesAsync(ct);
            return airline.Id;
        }
    }

    public sealed class CreateAirlineCommandValidator : AbstractValidator<CreateAirlineCommand>
    {
        public CreateAirlineCommandValidator()
        {
            RuleFor(x => x.Iata)
                .MaximumLength(3)
                .Matches(@"^[A-Z0-9]{2,3}$", RegexOptions.IgnoreCase).WithMessage("IATA must be 2–3 letters or digits.")
                .When(x => x.Iata is not null);
            RuleFor(x => x.Icao)
                .MaximumLength(4)
                .Matches(@"^[A-Z]{3,4}$", RegexOptions.IgnoreCase).WithMessage("ICAO must be 3–4 letters.")
                .When(x => x.Icao is not null);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.CountryCode)
                .NotEmpty().MaximumLength(2)
                .Matches(@"^[A-Z]{2}$", RegexOptions.IgnoreCase).WithMessage("Country code must be ISO 3166-1 alpha-2.");
            RuleFor(x => x.AllianceCode).MaximumLength(8).When(x => x.AllianceCode is not null);
        }
    }
}
