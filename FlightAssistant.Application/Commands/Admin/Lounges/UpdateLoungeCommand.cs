using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Lounges
{
    public record UpdateLoungeCommand(
        int Id,
        string Name,
        string? Terminal,
        string? LocationHint,
        string? Hours,
        bool IsAirside,
        bool IsDomesticOnly,
        bool IsInternationalOnly,
        string? Amenities
    ) : IRequest;

    public sealed class UpdateLoungeCommandHandler : IRequestHandler<UpdateLoungeCommand>
    {
        private readonly ILoungeRepository _repo;
        public UpdateLoungeCommandHandler(ILoungeRepository repo) => _repo = repo;

        public async Task Handle(UpdateLoungeCommand cmd, CancellationToken ct)
        {
            var lounge = await _repo.GetByIdWithProvidersAsync(cmd.Id, ct)
                ?? throw new DomainException($"Lounge {cmd.Id} not found.", 404);
            lounge.Update(cmd.Name, cmd.Terminal, cmd.LocationHint, cmd.Hours,
                cmd.IsAirside, cmd.IsDomesticOnly, cmd.IsInternationalOnly, cmd.Amenities);
            await _repo.SaveChangesAsync(ct);
        }
    }

    public sealed class UpdateLoungeCommandValidator : AbstractValidator<UpdateLoungeCommand>
    {
        public UpdateLoungeCommandValidator()
        {
            RuleFor(x => x.Id).GreaterThan(0);
            RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            RuleFor(x => x.Terminal).MaximumLength(32).When(x => x.Terminal is not null);
            RuleFor(x => x.LocationHint).MaximumLength(200).When(x => x.LocationHint is not null);
            RuleFor(x => x.Hours).MaximumLength(200).When(x => x.Hours is not null);
            RuleFor(x => x.Amenities).MaximumLength(1000).When(x => x.Amenities is not null);
        }
    }
}
