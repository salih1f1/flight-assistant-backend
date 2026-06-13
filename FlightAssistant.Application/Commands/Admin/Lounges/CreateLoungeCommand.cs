using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Lounges.Entities;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Lounges
{
    public record CreateLoungeCommand(
        int AirportId,
        string Name,
        string? Terminal,
        string? LocationHint,
        string? Hours,
        bool IsAirside,
        bool IsDomesticOnly,
        bool IsInternationalOnly,
        string? Amenities
    ) : IRequest<int>;

    public sealed class CreateLoungeCommandHandler : IRequestHandler<CreateLoungeCommand, int>
    {
        private readonly ILoungeRepository _repo;
        public CreateLoungeCommandHandler(ILoungeRepository repo) => _repo = repo;

        public async Task<int> Handle(CreateLoungeCommand cmd, CancellationToken ct)
        {
            var lounge = Lounge.Create(cmd.AirportId, cmd.Name, cmd.Terminal, cmd.LocationHint,
                cmd.Hours, cmd.IsAirside, cmd.IsDomesticOnly, cmd.IsInternationalOnly, cmd.Amenities);
            await _repo.AddAsync(lounge, ct);
            await _repo.SaveChangesAsync(ct);
            return lounge.Id;
        }
    }

    public sealed class CreateLoungeCommandValidator : AbstractValidator<CreateLoungeCommand>
    {
        public CreateLoungeCommandValidator()
        {
            RuleFor(x => x.AirportId).GreaterThan(0);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Terminal).MaximumLength(32).When(x => x.Terminal is not null);
            RuleFor(x => x.LocationHint).MaximumLength(200).When(x => x.LocationHint is not null);
            RuleFor(x => x.Hours).MaximumLength(200).When(x => x.Hours is not null);
            RuleFor(x => x.Amenities).MaximumLength(1000).When(x => x.Amenities is not null);
        }
    }
}
