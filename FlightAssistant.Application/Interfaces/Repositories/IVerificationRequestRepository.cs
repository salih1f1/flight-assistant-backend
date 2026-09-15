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
    /// </summary>
    public interface IVerificationRequestRepository
    {
        /// <summary>
        /// Sets every <c>Running</c> request whose <c>StartedUtc</c> is earlier than
        /// <paramref name="startedBeforeUtc"/> back to <c>Pending</c> and clears its
        /// <c>StartedUtc</c>. <c>Attempts</c> and <c>NextAttemptUtc</c> are unchanged. A request
        /// started exactly at the cutoff is left alone. The job passes now − 10 minutes.
        /// </summary>
        /// <param name="startedBeforeUtc">Must have <see cref="DateTimeKind.Utc"/>.</param>
        /// <returns>How many requests were reset.</returns>
        Task<int> ResetStuckRunningAsync(DateTime startedBeforeUtc, CancellationToken ct = default);

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

        /// <summary>Saves tracked changes, e.g. an outcome applied to a claimed request.</summary>
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
