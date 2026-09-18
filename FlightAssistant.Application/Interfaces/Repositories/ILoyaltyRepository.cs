using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Loyalty.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface ILoyaltyRepository : IRepository<LoyaltyProgram>
    {
        Task<(IReadOnlyList<LoyaltyProgram> Items, int Total)> GetProgramsAsync(int page, int pageSize, CancellationToken ct = default);
        Task<(IReadOnlyList<TravelerLoyaltyMembership> Items, int Total)> GetMembershipsForTravelerAsync(Guid travelerId, int page, int pageSize, CancellationToken ct = default);

        /// <summary>Every membership the traveler holds, unpaged — for the admin traveler
        /// drill-down (#322), which returns the whole relationship in one call. The unpaged
        /// sibling of <see cref="GetMembershipsForTravelerAsync"/>.</summary>
        Task<IReadOnlyList<TravelerLoyaltyMembership>> GetAllMembershipsForTravelerAsync(Guid travelerId, CancellationToken ct = default);
        Task<TravelerLoyaltyMembership?> GetMembershipByIdAsync(Guid id, CancellationToken ct = default);
        Task AddMembershipAsync(TravelerLoyaltyMembership membership, CancellationToken ct = default);
        Task RemoveMembershipAsync(TravelerLoyaltyMembership membership, CancellationToken ct = default);
        Task<LoyaltyProgram?> GetProgramByIdAsync(int id, CancellationToken ct = default);
        Task AddProgramAsync(LoyaltyProgram program, CancellationToken ct = default);
        Task DeleteProgramAsync(LoyaltyProgram program, CancellationToken ct = default);
        Task<IReadOnlyList<LoyaltyProgram>> SearchProgramsByAirlineIdAsync(int airlineId, CancellationToken ct = default);
        Task<IReadOnlyList<LoyaltyProgram>> GetAllVerifiedWithTiersAsync(CancellationToken ct = default);
        Task<IReadOnlyDictionary<int, string>> GetKindCodeMapAsync(CancellationToken ct = default);
        Task<LoyaltyProgramKind?> FindKindByCodeAsync(string code, CancellationToken ct = default);
        Task<LoyaltyProgramKind?> FindKindByIdAsync(int id, CancellationToken ct = default);
        Task AddKindAsync(LoyaltyProgramKind kind, CancellationToken ct = default);
        Task<LoyaltyProgram?> FindProgramByCodeWithTiersAsync(string code, CancellationToken ct = default);
    }
}
