using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Loyalty
{
    public record CreateLoyaltyProgramCommand(
        string Code,
        string Name,
        int KindId,
        int? AirlineId,
        string? AllianceCode
    ) : IRequest<int>;

    public sealed class CreateLoyaltyProgramCommandHandler : IRequestHandler<CreateLoyaltyProgramCommand, int>
    {
        private readonly ILoyaltyRepository _repo;
        public CreateLoyaltyProgramCommandHandler(ILoyaltyRepository repo) => _repo = repo;

        public async Task<int> Handle(CreateLoyaltyProgramCommand cmd, CancellationToken ct)
        {
            var program = LoyaltyProgram.Create(cmd.Code, cmd.Name, cmd.KindId, cmd.AirlineId, cmd.AllianceCode);
            await _repo.AddProgramAsync(program, ct);
            await _repo.SaveChangesAsync(ct);
            return program.Id;
        }
    }
}
