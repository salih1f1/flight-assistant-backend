using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;

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
        /// <param name="excludeIds">Ids never claimed by this call, however otherwise eligible. Null
        /// or empty excludes nothing. Was the G1 replacement-batch mechanism (#401 final fix); #428
        /// replaced that with <paramref name="excludeSourceNames"/> instead (see there for why), so
        /// this parameter has no production caller today. Kept because the claim's underlying
        /// id-exclusion SQL had to stay untouched (#428 R5) and it is still directly tested.</param>
        /// <param name="excludeSourceNames">Source names never claimed by this call: a request is
        /// skipped when any one of its own (comma-joined) source names exactly matches one of these,
        /// e.g. excluding <c>"aeroapi"</c> skips a request naming <c>"aeroapi"</c> alone or
        /// <c>"ourairports,aeroapi"</c>, but never one naming only <c>"aeroapi-v2"</c> -- matching is
        /// whole-name, never a substring (#428 R2). Null or empty excludes nothing (#428 R3: always
        /// sent to PostgreSQL as a concrete array, never a raw NULL, which would silently exclude
        /// every row instead of none). Exists so a cycle that just paused a request for the AeroAPI
        /// budget (#401 final fix G1) can claim a replacement batch that skips every request naming
        /// that source, not only the ones just paused (#428): unchanged <c>requested_utc</c>/<c>id</c>
        /// puts a paused request back at the head of the claim order, so excluding by id alone still
        /// lets the replacement batch fill up on other, not-yet-tried requests naming the same gated
        /// source when the backlog is at least two batches deep -- pausing them too and never reaching
        /// the free-source work behind them. R5 still forbids touching <c>next_attempt_utc</c>,
        /// <c>attempts</c> or <c>error</c> to solve this instead.</param>
        Task<IReadOnlyList<VerificationRequest>> ClaimBatchAsync(
            int batchSize, DateTime nowUtc, IReadOnlyCollection<long>? excludeIds = null,
            IReadOnlyCollection<string>? excludeSourceNames = null, CancellationToken ct = default);

        /// <summary>How many requests are <c>Pending</c> (due or not). Running requests are not counted.</summary>
        Task<int> CountPendingAsync(CancellationToken ct = default);

        /// <summary>
        /// The 24-hour Error-attribution report (#424): every request that completed as
        /// <see cref="VerificationRequestStatus.Error"/> at or after <c>now</c> (this repository's own
        /// <c>TimeProvider</c>) minus <see cref="ErrorAttributionWindowHours"/> hours, grouped by
        /// <c>(FailedSource, EntityType)</c> with each group's count. A null <c>FailedSource</c> is its
        /// own group -- an unattributed failure (a deleted entity, an unexpected exception, an
        /// abandoned claim) is reported, not dropped (R3). Only non-empty groups come back; a
        /// <c>GROUP BY</c> gives that for free (R5). Ordered by <c>Count</c> descending, then
        /// <c>FailedSource</c>, then <c>EntityType</c> as a full tie-break, so two identical polls
        /// return groups in the same order (#424 fix wave) -- the database gives no order for free to
        /// a <c>GROUP BY</c>. <c>AsNoTracking</c>; not audited (a read).
        /// </summary>
        Task<IReadOnlyList<VerificationFailureGroup>> GetRecentFailureGroupsAsync(CancellationToken ct = default);

        /// <summary>
        /// The rolling window <see cref="GetRecentFailureGroupsAsync"/> reports over. A fixed constant,
        /// not configuration (R4): this is a reporting window, not a behaviour tunable, so changing it
        /// needs a deploy rather than a config edit.
        /// </summary>
        public const int ErrorAttributionWindowHours = 24;

        /// <summary>
        /// Every source name that at least one open (<c>Pending</c>/<c>Running</c>) request names, in
        /// one query: each request's comma-joined <c>sources</c> split into whole names, so
        /// <c>ourairports</c> never stands in for <c>ourairports2</c>. The job drops the cached dataset
        /// of every registered source not in this set.
        /// </summary>
        Task<IReadOnlyCollection<string>> GetSourceNamesInUseAsync(CancellationToken ct = default);

        /// <summary>
        /// Returns claims the job is giving up without an outcome (it is shutting down mid-batch) to
        /// <c>Pending</c>, in one statement: <c>StartedUtc</c> cleared; <c>Attempts</c>,
        /// <c>NextAttemptUtc</c> and <c>Error</c> unchanged, so no attempt is counted. A request is
        /// touched only while it is still <c>Running</c> with that claim's own <c>StartedUtc</c>: one
        /// another worker has reset and claimed since, or whose outcome was saved, is left alone. Runs
        /// SQL directly, like the claim, so it is not audited.
        /// </summary>
        /// <param name="claims">Each claim's <c>StartedUtc</c> must have <see cref="DateTimeKind.Utc"/>.</param>
        /// <returns>How many requests went back to Pending.</returns>
        Task<int> ReturnClaimsToPendingAsync(IReadOnlyCollection<VerificationClaim> claims, CancellationToken ct = default);

        /// <summary>
        /// Whether the entity has an open (<c>Pending</c>/<c>Running</c>) request, in one query.
        /// </summary>
        /// <param name="entityKey">Canonical, as a stored request's <c>EntityKey</c> is.</param>
        Task<bool> HasOpenRequestAsync(VerificationEntityType entityType, string entityKey, CancellationToken ct = default);

        /// <summary>The request with <paramref name="id"/>, tracked, or null.</summary>
        Task<VerificationRequest?> GetByIdAsync(long id, CancellationToken ct = default);

        /// <summary>
        /// The entity's most recent requests, newest first (<c>RequestedUtc</c> descending, then
        /// <c>Id</c> descending as the tiebreaker -- the database gives no order for free to two
        /// requests sharing a timestamp), at most <paramref name="limit"/> (#385, Task 1). A "latest
        /// few" read for the admin panel's record page, not a page: no total, and the full history
        /// stays browsable at <c>GET /admin/data/verification-requests</c>. Read-only:
        /// <c>AsNoTracking</c>, and not audited (a read).
        /// </summary>
        /// <param name="entityKey">Canonical, as a stored request's <c>EntityKey</c> is.</param>
        /// <param name="limit">At least 1.</param>
        Task<IReadOnlyList<VerificationRequest>> GetRecentForEntityAsync(
            VerificationEntityType entityType, string entityKey, int limit, CancellationToken ct = default);

        /// <summary>
        /// Saves tracked changes, e.g. an outcome applied to a claimed request, in one transaction.
        /// Throws <see cref="VerificationClaimLostException"/> when a tracked request changed in the
        /// database since it was read, and then nothing from this save is written. Treat that as
        /// "claim lost" (see the interface remarks), not as a source error. Any other concurrency
        /// failure is rethrown as it is.
        /// </summary>
        Task SaveChangesAsync(CancellationToken ct = default);
    }

    /// <summary>What <see cref="IVerificationRequestRepository.ResetStuckRunningAsync"/> did.</summary>
    /// <param name="ReturnedToPending">Stuck requests returned to Pending, one attempt added.</param>
    /// <param name="Abandoned">Stuck requests that ran out of attempts and completed as Error.</param>
    public sealed record VerificationStuckResetResult(int ReturnedToPending, int Abandoned);

    /// <summary>
    /// One group from <see cref="IVerificationRequestRepository.GetRecentFailureGroupsAsync"/>: every
    /// <c>Error</c> completion in the report's window sharing one <c>(FailedSource, EntityType)</c> pair.
    /// </summary>
    /// <param name="FailedSource">The source attributable for the failure, or null when it isn't
    /// attributable to one named source (R3: reported as its own "unattributed" group, never dropped).</param>
    /// <param name="EntityType">The entity type the failing checks targeted.</param>
    /// <param name="Count">How many Error completions in the window matched this group.</param>
    public sealed record VerificationFailureGroup(string? FailedSource, VerificationEntityType EntityType, int Count);
}
