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

        /// <summary>
        /// The name of each of these airlines' own loyalty programs, keyed by airline id — the bulk
        /// sibling of <see cref="SearchProgramsByAirlineIdAsync"/>, for callers holding a list of
        /// airlines rather than one (the alliance drill-down, #495). Airlines with no program of
        /// their own are absent from the dictionary rather than present with a null.
        /// <para>
        /// An airline may run several programs; the one named is the first by name, so that two
        /// identical calls answer the same way whatever order the database returns rows in.
        /// </para>
        /// </summary>
        Task<IReadOnlyDictionary<int, string>> GetProgramNamesByAirlineIdsAsync(IReadOnlyCollection<int> airlineIds, CancellationToken ct = default);
        Task<IReadOnlyList<LoyaltyProgram>> GetAllVerifiedWithTiersAsync(CancellationToken ct = default);
        Task<IReadOnlyDictionary<int, string>> GetKindCodeMapAsync(CancellationToken ct = default);
        Task<LoyaltyProgramKind?> FindKindByCodeAsync(string code, CancellationToken ct = default);
        Task<LoyaltyProgramKind?> FindKindByIdAsync(int id, CancellationToken ct = default);
        Task AddKindAsync(LoyaltyProgramKind kind, CancellationToken ct = default);
        Task<LoyaltyProgram?> FindProgramByCodeWithTiersAsync(string code, CancellationToken ct = default);
    }
}
