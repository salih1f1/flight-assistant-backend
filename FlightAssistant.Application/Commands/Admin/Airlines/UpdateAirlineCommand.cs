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
        public UpdateAirlineCommandHandler(IAirlineRepository repo) => _repo = repo;

        public async Task Handle(UpdateAirlineCommand cmd, CancellationToken ct)
        {
            var airline = await _repo.GetByIdAsync(cmd.Id, ct)
                ?? throw new DomainException($"Airline {cmd.Id} not found.", 404);
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
                .Matches(@"^[A-Z0-9]{2,3}$").WithMessage("IATA must be 2–3 uppercase letters or digits.")
                .When(x => x.Iata is not null);
            RuleFor(x => x.Icao)
                .MaximumLength(4)
                .Matches(@"^[A-Z]{3,4}$").WithMessage("ICAO must be 3–4 uppercase letters.")
                .When(x => x.Icao is not null);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.CountryCode)
                .NotEmpty().MaximumLength(2)
                .Matches(@"^[A-Z]{2}$").WithMessage("Country code must be ISO 3166-1 alpha-2.");
            RuleFor(x => x.AllianceCode).MaximumLength(8).When(x => x.AllianceCode is not null);
        }
    }
}
