using System.Text.RegularExpressions;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Airports
{
    public record UpdateAirportCommand(
        int Id,
        string Iata,
        string? Icao,
        string Name,
        string City,
        string CountryCode,
        double? Latitude,
        double? Longitude,
        int? ElevationFt
    ) : IRequest;

    public sealed class UpdateAirportCommandHandler : IRequestHandler<UpdateAirportCommand>
    {
        private readonly IAirportRepository _repo;
        public UpdateAirportCommandHandler(IAirportRepository repo) => _repo = repo;

        public async Task Handle(UpdateAirportCommand cmd, CancellationToken ct)
        {
            var airport = await _repo.GetByIdAsync(cmd.Id, ct)
                ?? throw new DomainException($"Airport {cmd.Id} not found.", 404);
            airport.Update(cmd.Iata, cmd.Icao, cmd.Name, cmd.City, cmd.CountryCode,
                cmd.Latitude, cmd.Longitude, cmd.ElevationFt);
            await _repo.SaveChangesAsync(ct);
        }
    }

    public sealed class UpdateAirportCommandValidator : AbstractValidator<UpdateAirportCommand>
    {
        public UpdateAirportCommandValidator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
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
        }
    }
}
