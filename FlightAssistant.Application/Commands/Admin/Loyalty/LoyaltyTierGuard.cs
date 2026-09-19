using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Loyalty.Entities;

namespace FlightAssistant.Application.Commands.Admin.Loyalty
{
    /// <summary>
    /// The checks every tier write shares (#488). A tier is addressed through the program that owns
    /// it, so both lookups start from the program aggregate — which is also what keeps one program's
    /// route from reaching another program's tier.
    /// </summary>
    internal static class LoyaltyTierGuard
    {
        public static LoyaltyProgram EnsureProgram(LoyaltyProgram? program, int programId) =>
            program ?? throw new DomainException($"Loyalty program {programId} not found.", 404);

        /// <summary>
        /// The program's tier with this id. A tier belonging to another program is not found here,
        /// and is reported as a 404 rather than leaking that it exists elsewhere.
        /// </summary>
        public static LoyaltyTier EnsureTier(LoyaltyProgram program, int tierId) =>
            program.Tiers.FirstOrDefault(t => t.Id == tierId)
            ?? throw new DomainException($"Loyalty tier {tierId} not found on program {program.Id}.", 404);

        /// <summary>
        /// loyalty_tiers is unique on (ProgramId, Code) and on (ProgramId, Rank). Checking here makes
        /// a clash a 409 the panel can show; left to the database, the index failure would surface as
        /// a 500. <paramref name="exceptTierId"/> skips the tier being edited, so keeping its own code
        /// and rank is not a conflict with itself.
        /// </summary>
        public static void EnsureNoClash(LoyaltyProgram program, string code, int rank, int? exceptTierId = null)
        {
            var others = program.Tiers.Where(t => t.Id != exceptTierId).ToList();

            if (others.Any(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase)))
                throw new DomainException($"Loyalty program {program.Id} already has a tier with code {code}.", 409);

            if (others.Any(t => t.Rank == rank))
                throw new DomainException($"Loyalty program {program.Id} already has a tier at rank {rank}.", 409);
        }
    }
}
