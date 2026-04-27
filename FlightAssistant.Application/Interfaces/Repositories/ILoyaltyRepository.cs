using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Loyalty.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface ILoyaltyRepository : IRepository<LoyaltyProgram>
    {
        Task<(IReadOnlyList<LoyaltyProgram> Items, int Total)> GetProgramsAsync(int page, int pageSize, CancellationToken ct = default);
        Task<(IReadOnlyList<TravelerLoyaltyMembership> Items, int Total)> GetMembershipsForTravelerAsync(Guid travelerId, int page, int pageSize, CancellationToken ct = default);
        Task<TravelerLoyaltyMembership?> GetMembershipByIdAsync(Guid id, CancellationToken ct = default);
        Task AddMembershipAsync(TravelerLoyaltyMembership membership, CancellationToken ct = default);
        Task RemoveMembershipAsync(TravelerLoyaltyMembership membership, CancellationToken ct = default);
        Task<LoyaltyProgram?> GetProgramByIdAsync(int id, CancellationToken ct = default);
        Task AddProgramAsync(LoyaltyProgram program, CancellationToken ct = default);
        Task DeleteProgramAsync(LoyaltyProgram program, CancellationToken ct = default);
    }
}
