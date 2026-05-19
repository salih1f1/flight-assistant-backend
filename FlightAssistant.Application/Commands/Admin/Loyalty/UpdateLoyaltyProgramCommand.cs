using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Loyalty
{
    public record UpdateLoyaltyProgramCommand(
        int Id,
        string Code,
        string Name,
        int KindId,
        int? AirlineId,
        string? AllianceCode
    ) : IRequest;

    public sealed class UpdateLoyaltyProgramCommandHandler : IRequestHandler<UpdateLoyaltyProgramCommand>
    {
        private readonly ILoyaltyRepository _repo;
        public UpdateLoyaltyProgramCommandHandler(ILoyaltyRepository repo) => _repo = repo;

        public async Task Handle(UpdateLoyaltyProgramCommand cmd, CancellationToken ct)
        {
            var program = await _repo.GetProgramByIdAsync(cmd.Id, ct)
                ?? throw new DomainException($"Loyalty program {cmd.Id} not found.", 404);
            program.Update(cmd.Code, cmd.Name, cmd.KindId, cmd.AirlineId, cmd.AllianceCode);
            await _repo.SaveChangesAsync(ct);
        }
    }
}
