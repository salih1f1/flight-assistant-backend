using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Lounges
{
    public record DeleteLoungeCommand(int Id) : IRequest;

    public sealed class DeleteLoungeCommandHandler : IRequestHandler<DeleteLoungeCommand>
    {
        private readonly ILoungeRepository _repo;
        public DeleteLoungeCommandHandler(ILoungeRepository repo) => _repo = repo;

        public async Task Handle(DeleteLoungeCommand cmd, CancellationToken ct)
        {
            var lounge = await _repo.GetByIdWithProvidersAsync(cmd.Id, ct);
            if (lounge is null) return;
            await _repo.DeleteAsync(lounge, ct);
            await _repo.SaveChangesAsync(ct);
        }
    }
}
