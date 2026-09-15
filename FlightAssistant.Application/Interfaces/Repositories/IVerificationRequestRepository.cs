using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    /// <summary>
    /// The verification job's access to the <c>verification_requests</c> queue (spec §4.2).
    /// Queuing goes through <see cref="Verification.IVerificationQueue"/> instead.
    ///
    /// <para>
    /// <see cref="ResetStuckRunningAsync"/> and <see cref="ClaimBatchAsync"/> run SQL directly, so
    /// they bypass the change tracker (and the audit interceptor, which skips this table anyway),
    /// need PostgreSQL, and commit on their own unless the caller has an open transaction.
    /// </para>
    ///
    /// <para>
    /// <b>Claim lost.</b> Every request carries a PostgreSQL <c>xmin</c> concurrency token. If a
    /// request changed after this context read it (typically a slow claim that
    /// <see cref="ResetStuckRunningAsync"/> returned to <c>Pending</c> and another cycle claimed
    /// again), <see cref="SaveChangesAsync"/> throws <see cref="VerificationClaimLostException"/>
    /// (wrapping EF Core's <c>DbUpdateConcurrencyException</c>, which Application cannot reference)
    /// and the <b>whole</b> save rolls back, including any entity <c>Verify</c> or
    /// <c>MarkVerificationFailed</c> and its audit row. Callers must treat that as "claim lost":
    /// discard the result and the scope's context, and do <b>not</b> record it as a source error.
    /// The newer claim owns the request.
    /// </para>
    /// </summary>
    /// <summary>What <see cref="IVerificationRequestRepository.ResetStuckRunningAsync"/> did.</summary>
    /// <param name="ReturnedToPending">Stuck requests returned to Pending, one attempt added.</param>
    /// <param name="Abandoned">Stuck requests that ran out of attempts and completed as Error.</param>
    public sealed record VerificationStuckResetResult(int ReturnedToPending, int Abandoned);

    public interface IVerificationRequestRepository
    {
        /// <summary>
        /// Finds every <c>Running</c> request whose <c>StartedUtc</c> is earlier than
        /// <paramref name="startedBeforeUtc"/> (a request started exactly at the cutoff is left alone;
        /// the job passes now − 10 minutes), counts its abandoned claim as an attempt, and clears its
        /// <c>StartedUtc</c>, in one statement:
        /// <list type="bullet">
        /// <item>below <paramref name="maxAttempts"/> it goes back to <c>Pending</c>, with its
        /// <c>NextAttemptUtc</c> and <c>Error</c> unchanged;</item>
        /// <item>once the incremented <c>Attempts</c> reaches <paramref name="maxAttempts"/> it completes
        /// as <c>Error</c> with <c>VerificationRequest.ClaimAbandonedError(attempts)</c>,
        /// <c>CompletedUtc = nowUtc</c> and no <c>NextAttemptUtc</c>, so a request that never records a
        /// result cannot come back forever.</item>
        /// </list>
        /// Not audited: the statement bypasses the change tracker, and queue rows are not audited anyway.
        /// </summary>
        /// <param name="startedBeforeUtc">Must have <see cref="DateTimeKind.Utc"/>.</param>
        /// <param name="maxAttempts">The job's <c>MaxAttempts</c>; at least 1.</param>
        /// <param name="nowUtc">Stamped as <c>CompletedUtc</c> on abandoned requests; must have
        /// <see cref="DateTimeKind.Utc"/>.</param>
        Task<VerificationStuckResetResult> ResetStuckRunningAsync(
            DateTime startedBeforeUtc, int maxAttempts, DateTime nowUtc, CancellationToken ct = default);

        /// <summary>
        /// Claims up to <paramref name="batchSize"/> <c>Pending</c> requests that are due
        /// (<c>NextAttemptUtc</c> null or at or before <paramref name="nowUtc"/>), in one statement:
        /// sets them <c>Running</c> with <c>StartedUtc = nowUtc</c> and returns them. Picks
        /// <c>flight</c>-triggered requests first, then the oldest <c>RequestedUtc</c>, then the
        /// lowest id, and returns them in that order.
        ///
        /// <para>
        /// Rows are selected <c>FOR UPDATE SKIP LOCKED</c>, so concurrent claims (another job
        /// instance, another connection) never return the same request: a row another transaction
        /// has locked is skipped rather than waited for.
        /// </para>
        ///
        /// <para>
        /// The returned entities are <b>tracked</b> by this repository's context, so the caller
        /// can apply an outcome and <see cref="SaveChangesAsync"/>. Call it on a context that is not
        /// already tracking any of these requests (the job uses a fresh scope per cycle): EF would
        /// hand back the already-tracked instance, with its stale status.
        /// </para>
        ///
        /// <para>
        /// Each returned request carries the concurrency token of this claim. If the claim is
        /// lost before the outcome is saved, <see cref="SaveChangesAsync"/> throws
        /// <see cref="VerificationClaimLostException"/>; see "Claim lost" on the interface.
        /// </para>
        /// </summary>
        /// <param name="batchSize">At least 1.</param>
        /// <param name="nowUtc">Must have <see cref="DateTimeKind.Utc"/>.</param>
        Task<IReadOnlyList<VerificationRequest>> ClaimBatchAsync(int batchSize, DateTime nowUtc, CancellationToken ct = default);

        /// <summary>How many requests are <c>Pending</c> (due or not). Running requests are not counted.</summary>
        Task<int> CountPendingAsync(CancellationToken ct = default);

        /// <summary>
        /// Whether any open (<c>Pending</c>/<c>Running</c>) request names
        /// <paramref name="sourceName"/> among its sources. Matches whole names only:
        /// <c>ourairports</c> does not match a request for <c>ourairports2</c>. The job drops a
        /// source's cached dataset when this is false.
        /// </summary>
        Task<bool> AnyOpenRequestUsesSourceAsync(string sourceName, CancellationToken ct = default);

        /// <summary>The request with <paramref name="id"/>, tracked, or null.</summary>
        Task<VerificationRequest?> GetByIdAsync(long id, CancellationToken ct = default);

        /// <summary>
        /// Saves tracked changes, e.g. an outcome applied to a claimed request, in one transaction.
        /// Throws <see cref="VerificationClaimLostException"/> when a tracked request changed in the
        /// database since it was read, and then nothing from this save is written. Treat that as
        /// "claim lost" (see the interface remarks), not as a source error. Any other concurrency
        /// failure is rethrown as it is.
        /// </summary>
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
