using System.Text.RegularExpressions;
using FlightAssistant.Application.Commands.Admin.Common;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Airlines
{
    public record UpdateAirlineCommand(
        int Id,
        string? Iata,
        string? Icao,
        string Name,
        string CountryCode,
        string? AllianceCode
    ) : IRequest;

    public sealed class UpdateAirlineCommandHandler : IRequestHandler<UpdateAirlineCommand>
    {
        private readonly IAirlineRepository _repo;
        private readonly IAllianceRepository _alliances;
        private readonly ICountryRepository _countries;
        public UpdateAirlineCommandHandler(
            IAirlineRepository repo, IAllianceRepository alliances, ICountryRepository countries)
        {
            _repo = repo;
            _alliances = alliances;
            _countries = countries;
        }

        public async Task Handle(UpdateAirlineCommand cmd, CancellationToken ct)
        {
            var airline = await _repo.GetByIdAsync(cmd.Id, ct)
                ?? throw new DomainException($"Airline {cmd.Id} not found.", 404);
            await ReferenceExistence.EnsureAllianceExistsAsync(_alliances, cmd.AllianceCode, "Airline", ct);
            await ReferenceExistence.EnsureCountryExistsAsync(_countries, cmd.CountryCode, "Airline", ct);
            airline.Update(cmd.Iata, cmd.Icao, cmd.Name, cmd.CountryCode, cmd.AllianceCode);
            await _repo.SaveChangesAsync(ct);
        }
    }

    public sealed class UpdateAirlineCommandValidator : AbstractValidator<UpdateAirlineCommand>
    {
        public UpdateAirlineCommandValidator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
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
