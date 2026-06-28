using System.Text.RegularExpressions;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Airports
{
    public record CreateAirportCommand(
        string Iata,
        string? Icao,
        string Name,
        string City,
        string CountryCode,
        double? Latitude,
        double? Longitude,
        int? ElevationFt,
        string? Timezone
    ) : IRequest<int>;

    public sealed class CreateAirportCommandHandler : IRequestHandler<CreateAirportCommand, int>
    {
        private readonly IAirportRepository _repo;
        public CreateAirportCommandHandler(IAirportRepository repo) => _repo = repo;

        public async Task<int> Handle(CreateAirportCommand cmd, CancellationToken ct)
        {
            var airport = Airport.Create(cmd.Iata, cmd.Icao, cmd.Name, cmd.City, cmd.CountryCode,
                cmd.Latitude, cmd.Longitude, cmd.ElevationFt, cmd.Timezone);
            await _repo.AddAsync(airport, ct);
            await _repo.SaveChangesAsync(ct);
            return airport.Id;
        }
    }

    public sealed class CreateAirportCommandValidator : AbstractValidator<CreateAirportCommand>
    {
        public CreateAirportCommandValidator()
        {
            RuleFor(x => x.Iata)
                .NotEmpty().MaximumLength(3)
                .Matches(@"^[A-Z]{3}$", RegexOptions.IgnoreCase).WithMessage("IATA must be exactly 3 letters.");
            RuleFor(x => x.Icao)
                .MaximumLength(4)
                .Matches(@"^[A-Z]{4}$", RegexOptions.IgnoreCase).WithMessage("ICAO must be exactly 4 letters.")
                .When(x => x.Icao is not null);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.City).NotEmpty().MaximumLength(200);
            RuleFor(x => x.CountryCode)
                .NotEmpty().MaximumLength(2)
                .Matches(@"^[A-Z]{2}$", RegexOptions.IgnoreCase).WithMessage("Country code must be ISO 3166-1 alpha-2.");
            RuleFor(x => x.Timezone).MaximumLength(64).When(x => x.Timezone is not null);
        }
    }
}
