using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    /// <summary>One <see cref="VerificationEntityType"/>'s needs-attention count (#385, Task 2).</summary>
    /// <param name="EntityType">Airport, Airline or Country.</param>
    /// <param name="Count">Rows in the needs-attention state (spec §2.1): <c>IsVerified</c> false and
    /// <c>VerificationIssue</c> non-null and non-blank.</param>
    public sealed record VerificationNeedsAttentionCount(VerificationEntityType EntityType, int Count);

    /// <summary>
    /// Counts rows in the needs-attention state across the three verifiable reference-data tables
    /// (#385, Task 2; backs <c>GET /admin/verification-needs-attention</c>). Its own port rather than a
    /// method on an existing repository: <see cref="IRefDataRepository"/> is documented as IATA ↔ id
    /// primitive resolution and reads no <c>Country</c> at all, and <see cref="IVerificationRequestRepository"/>
    /// is the <c>verification_requests</c> queue, a different table entirely. Both would have to grow a
    /// reason to read <c>Airport</c>/<c>Airline</c>/<c>Country</c> that isn't really theirs.
    /// </summary>
    public interface IVerificationNeedsAttentionRepository
    {
        /// <summary>
        /// One entry per <see cref="VerificationEntityType"/>, always all three, in enum order, with a
        /// count of 0 rather than an omitted entry (controller ruling R3). Three <c>CountAsync</c>
        /// queries, one per table, with no client-side filtering: "needs attention" (spec §2.1) is
        /// <c>IsVerified</c> false <b>and</b> <c>VerificationIssue</c> non-null and non-blank -- only the
        /// verification job's mismatch and not-found paths ever set one, so a row that has simply never
        /// been checked (<c>IsVerified</c> false, no issue) does not count.
        /// </summary>
        Task<IReadOnlyList<VerificationNeedsAttentionCount>> GetCountsAsync(CancellationToken ct = default);
    }
}
