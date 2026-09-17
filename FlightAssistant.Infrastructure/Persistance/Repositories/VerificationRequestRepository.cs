using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.Persistance.Configurations.Verification;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    /// <summary>
    /// The <c>verification_requests</c> queue (#367): enqueueing for callers, and claiming, resetting
    /// and saving for the job. One class behind both interfaces because both are the same table.
    /// </summary>
    public sealed class VerificationRequestRepository : IVerificationQueue, IVerificationRequestRepository
    {
        private readonly FlightAssistantDbContext _db;
        private readonly TimeProvider _timeProvider;
        private readonly IVerificationJobOptions _options;

        public VerificationRequestRepository(FlightAssistantDbContext db, TimeProvider timeProvider, IVerificationJobOptions options)
        {
            _db = db;
            _timeProvider = timeProvider;
            _options = options;
        }

        public async Task<VerificationEnqueueResult> EnqueueAsync(
            VerificationEntityType entityType,
            IReadOnlyCollection<string> keys,
            IReadOnlyCollection<string> sources,
            VerificationTrigger trigger,
            string requestedBy,
            decimal estimatedCostUsd,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(keys);
            ArgumentNullException.ThrowIfNull(sources);

            var requestedUtc = _timeProvider.GetUtcNow().UtcDateTime;
            VerificationRequest NewRequest(string key) =>
                VerificationRequest.Create(entityType, key, sources, trigger, requestedBy, estimatedCostUsd, requestedUtc);

            // Built first: Create validates every argument (so invalid input throws even when every
            // key would be skipped) and canonicalises each key, so "gb" and "GB" are one key below.
            var candidates = keys.Select(NewRequest).ToList();
            var distinct = candidates.DistinctBy(r => r.EntityKey, StringComparer.Ordinal).ToList();
            var skipped = candidates.Count - distinct.Count; // a repeated key: its first occurrence is queued or skipped
            var distinctKeys = distinct.Select(r => r.EntityKey).ToList();

            var alreadyOpen = (await _db.VerificationRequests
                    .AsNoTracking()
                    .Where(r => r.EntityType == entityType
                        && distinctKeys.Contains(r.EntityKey)
                        && (r.Status == VerificationRequestStatus.Pending || r.Status == VerificationRequestStatus.Running))
                    .Select(r => r.EntityKey)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            var toQueue = distinct.Where(r => !alreadyOpen.Contains(r.EntityKey)).ToList();
            skipped += distinct.Count - toQueue.Count;
            if (toQueue.Count == 0) return new VerificationEnqueueResult(0, skipped);

            // #418: flight-tracking trigger only (R2) -- RetryVerificationRequestCommandHandler turns
            // any Skipped > 0 into a 409 claiming an open request exists, so a cooldown skip must
            // never reach an admin retry/enqueue. TimeSpan.Zero disables the cooldown and skips this
            // query entirely (R6), matching IdleRelease's convention.
            if (trigger == VerificationTrigger.Flight && _options.ErrorCooldown > TimeSpan.Zero)
            {
                var cutoffUtc = requestedUtc - _options.ErrorCooldown;
                var toQueueKeys = toQueue.Select(r => r.EntityKey).ToList();

                // Which of the remaining candidates last completed as Error at or after cutoffUtc
                // (R3): an Error row in the window such that no other row for the same key completed
                // later -- a newer completion of any status (Verified/Mismatch/NotFound) supersedes
                // an older Error, since the most recent outcome is what counts. Mirrors alreadyOpen
                // above: one round trip, AsNoTracking, projecting just the keys into a HashSet -- the
                // shape ix_verification_requests_error_cooldown (entity_type, entity_key,
                // completed_utc, filtered to completed_utc IS NOT NULL, not status = Error --
                // fix round 1) exists to answer: the filter has to cover every completed row, not
                // only Error ones, because this query's second half (below) asks "is there a newer
                // completion of any status", which a status='Error' partial index cannot serve.
                var cooling = (await _db.VerificationRequests
                        .AsNoTracking()
                        .Where(r => r.EntityType == entityType
                            && toQueueKeys.Contains(r.EntityKey)
                            && r.Status == VerificationRequestStatus.Error
                            && r.CompletedUtc != null
                            && r.CompletedUtc >= cutoffUtc
                            && !_db.VerificationRequests.Any(newer =>
                                newer.EntityType == entityType
                                && newer.EntityKey == r.EntityKey
                                && newer.CompletedUtc != null
                                && newer.CompletedUtc > r.CompletedUtc))
                        .Select(r => r.EntityKey)
                        .Distinct()
                        .ToListAsync(ct))
                    .ToHashSet(StringComparer.Ordinal);

                if (cooling.Count > 0)
                {
                    toQueue = toQueue.Where(r => !cooling.Contains(r.EntityKey)).ToList();
                    skipped += cooling.Count;
                    if (toQueue.Count == 0) return new VerificationEnqueueResult(0, skipped);
                }
            }

            // Usual path: one save for the whole batch.
            _db.VerificationRequests.AddRange(toQueue);
            try
            {
                await _db.SaveChangesAsync(ct);
                return new VerificationEnqueueResult(toQueue.Count, skipped);
            }
            catch (DbUpdateException ex) when (IsOpenRequestConflict(ex))
            {
                // Another enqueue inserted one of these entities after the check above. The batch
                // rolled back as a whole, so retry key by key and count only the conflicts as skipped.
                foreach (var request in toQueue)
                    _db.Entry(request).State = EntityState.Detached;
            }

            var queued = 0;
            foreach (var key in toQueue.Select(r => r.EntityKey))
            {
                var request = NewRequest(key);
                _db.VerificationRequests.Add(request);
                try
                {
                    await _db.SaveChangesAsync(ct);
                    queued++;
                }
                catch (DbUpdateException ex) when (IsOpenRequestConflict(ex))
                {
                    _db.Entry(request).State = EntityState.Detached;
                    skipped++;
                }
            }

            return new VerificationEnqueueResult(queued, skipped);
        }

        public async Task<VerificationStuckResetResult> ResetStuckRunningAsync(
            DateTime startedBeforeUtc, int maxAttempts, DateTime nowUtc, CancellationToken ct = default)
        {
            EnsureUtc(startedBeforeUtc, nameof(startedBeforeUtc));
            ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
            EnsureUtc(nowUtc, nameof(nowUtc));

            var running = nameof(VerificationRequestStatus.Running);
            var pending = nameof(VerificationRequestStatus.Pending);
            var error = nameof(VerificationRequestStatus.Error);
            var abandonedPrefix = VerificationRequest.ClaimAbandonedErrorPrefix;
            var abandonedSuffix = VerificationRequest.ClaimAbandonedErrorSuffix;

            // One statement: every SET expression reads the row's old values, so each CASE sees the
            // same pre-increment attempts. RETURNING reports what each row became; AsNoTracking keeps
            // the rows out of this context (it composes no SQL). xmin is returned explicitly because
            // RETURNING v.* never includes system columns. Every value is a parameter.
            //
            // failed_source (#424): nulled on the same branch that overwrites error with the
            // abandoned-claim text -- abandonment is the final cause, not the source. Left unchanged
            // (v."failed_source", like v."error" beside it) on the branch that returns to Pending,
            // so it keeps mirroring error rather than drifting from it.
            var reset = await _db.VerificationRequests
                .FromSql($"""
                    UPDATE "verification_requests" AS v
                    SET "attempts" = v."attempts" + 1,
                        "status" = CASE WHEN v."attempts" + 1 >= {maxAttempts} THEN {error} ELSE {pending} END,
                        "error" = CASE WHEN v."attempts" + 1 >= {maxAttempts}
                            THEN {abandonedPrefix} || (v."attempts" + 1) || {abandonedSuffix}
                            ELSE v."error" END,
                        "failed_source" = CASE WHEN v."attempts" + 1 >= {maxAttempts} THEN NULL ELSE v."failed_source" END,
                        "completed_utc" = CASE WHEN v."attempts" + 1 >= {maxAttempts} THEN {nowUtc} ELSE v."completed_utc" END,
                        "next_attempt_utc" = CASE WHEN v."attempts" + 1 >= {maxAttempts} THEN NULL ELSE v."next_attempt_utc" END,
                        "started_utc" = NULL
                    WHERE v."status" = {running} AND v."started_utc" < {startedBeforeUtc}
                    RETURNING v.*, v."xmin"
                    """)
                .AsNoTracking()
                .ToListAsync(ct);

            return new VerificationStuckResetResult(
                ReturnedToPending: reset.Count(r => r.Status == VerificationRequestStatus.Pending),
                Abandoned: reset.Count(r => r.Status == VerificationRequestStatus.Error));
        }

        public async Task<IReadOnlyList<VerificationRequest>> ClaimBatchAsync(
            int batchSize, DateTime nowUtc, IReadOnlyCollection<long>? excludeIds = null,
            IReadOnlyCollection<string>? excludeSourceNames = null, CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
            EnsureUtc(nowUtc, nameof(nowUtc));

            var pending = nameof(VerificationRequestStatus.Pending);
            var running = nameof(VerificationRequestStatus.Running);
            var flight = VerificationRequestConfiguration.FlightTriggerValue;

            // Always concrete arrays, never null: ANY(NULL::bigint[]) is NULL, not false, and
            // array && NULL::text[] is likewise NULL, not false -- either would make its NOT(...)
            // predicate NULL too and silently exclude every row instead of none (#428 R3).
            var exclude = excludeIds?.ToArray() ?? Array.Empty<long>();
            var excludeSources = excludeSourceNames?.ToArray() ?? Array.Empty<string>();

            // One statement, so choosing rows and marking them Running cannot be separated by another
            // claimer. The CTE picks at most batchSize ids exactly once; an IN (subquery) form can be
            // rescanned by the planner and claim more. FOR UPDATE locks the chosen rows until this
            // statement's transaction ends; SKIP LOCKED makes a concurrent claimer pass over them
            // instead of waiting and then claiming them a second time. xmin (the concurrency token)
            // is returned explicitly because RETURNING v.* never includes system columns. Every
            // value is a parameter.
            //
            // string_to_array(..., ',') && excludeSources (#428 R2): array-overlap over the whole
            // split names of the comma-joined "sources" column, never LIKE '%aeroapi%' -- a substring
            // match would also exclude an unrelated "aeroapi-v2" source. Sound because a source name
            // can never itself contain a comma (VerificationRequest.TryJoinSources forbids it).
            var claimed = await _db.VerificationRequests
                .FromSql($"""
                    WITH claimable AS (
                        SELECT "id"
                        FROM "verification_requests"
                        WHERE "status" = {pending}
                          AND ("next_attempt_utc" IS NULL OR "next_attempt_utc" <= {nowUtc})
                          AND NOT ("id" = ANY({exclude}))
                          AND NOT (string_to_array("sources", ',') && {excludeSources})
                        ORDER BY ("trigger" = {flight}) DESC, "requested_utc", "id"
                        LIMIT {batchSize}
                        FOR UPDATE SKIP LOCKED
                    )
                    UPDATE "verification_requests" AS v
                    SET "status" = {running}, "started_utc" = {nowUtc}
                    FROM claimable
                    WHERE v."id" = claimable."id"
                    RETURNING v.*, v."xmin"
                    """)
                .ToListAsync(ct);

            // RETURNING has no defined order; hand them back in claim order.
            return claimed
                .OrderByDescending(r => r.Trigger == VerificationTrigger.Flight)
                .ThenBy(r => r.RequestedUtc)
                .ThenBy(r => r.Id)
                .ToList();
        }

        public Task<int> CountPendingAsync(CancellationToken ct = default) =>
            _db.VerificationRequests.CountAsync(r => r.Status == VerificationRequestStatus.Pending, ct);

        public async Task<IReadOnlyList<VerificationFailureGroup>> GetRecentFailureGroupsAsync(CancellationToken ct = default)
        {
            // now from this repository's own TimeProvider (never DateTime.UtcNow), so the window is
            // testable with a FakeTimeProvider like every other time-dependent read here. UtcDateTime
            // carries DateTimeKind.Utc, which Npgsql requires against timestamptz.
            var windowStartUtc = _timeProvider.GetUtcNow().UtcDateTime
                - TimeSpan.FromHours(IVerificationRequestRepository.ErrorAttributionWindowHours);

            // Written to match, LINQ-side, the hand-written statement the index was proven against:
            // "WHERE status = 'Error' AND completed_utc >= @windowStart GROUP BY failed_source,
            // entity_type". The statement EF generates for this query is served by
            // ix_verification_requests_error_attribution as an index-only scan, with no sequential
            // scan (see CLAUDE.md). No explicit CompletedUtc != null check -- every Error row already
            // carries one (Complete() sets it), and a null CompletedUtc would compare false against
            // windowStartUtc anyway. AsNoTracking: a read, and grouped projections are never tracked
            // regardless. OrderByDescending(Count) puts the worst offender first, with FailedSource
            // then EntityType as a full tie-break: without an explicit order, Postgres can reshuffle
            // groups between two identical polls (a HashAggregate, or a switch to a sort-based one),
            // which would read to an admin panel as churn with no cause. The sort runs over a handful
            // of already-aggregated groups after the index-only scan above has done the real work, so
            // it adds no scan and does not change what the index serves.
            return await _db.VerificationRequests
                .AsNoTracking()
                .Where(r => r.Status == VerificationRequestStatus.Error && r.CompletedUtc >= windowStartUtc)
                .GroupBy(r => new { r.FailedSource, r.EntityType })
                .Select(g => new { g.Key.FailedSource, g.Key.EntityType, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.FailedSource)
                .ThenBy(g => g.EntityType)
                .Select(g => new VerificationFailureGroup(g.FailedSource, g.EntityType, g.Count))
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyCollection<string>> GetSourceNamesInUseAsync(CancellationToken ct = default)
        {
            // One query for the distinct joined lists (one per combination of sources in use, so few),
            // split into whole names here.
            var joinedLists = await _db.VerificationRequests
                .AsNoTracking()
                .Where(r => r.Status == VerificationRequestStatus.Pending || r.Status == VerificationRequestStatus.Running)
                .Select(r => r.Sources)
                .Distinct()
                .ToListAsync(ct);

            return joinedLists
                .SelectMany(joined => joined.Split(VerificationRequest.SourceSeparator))
                .ToHashSet(StringComparer.Ordinal);
        }

        public async Task<int> ReturnClaimsToPendingAsync(IReadOnlyCollection<VerificationClaim> claims, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(claims);
            if (claims.Count == 0) return 0;
            foreach (var claim in claims)
                EnsureUtc(claim.StartedUtc, nameof(claims));

            var pending = nameof(VerificationRequestStatus.Pending);
            var running = nameof(VerificationRequestStatus.Running);

            // Typed explicitly: an array's element type decides the PostgreSQL type, and started_utc
            // is timestamptz.
            var ids = new NpgsqlParameter("claim_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = claims.Select(c => c.RequestId).ToArray()
            };
            var startedUtcs = new NpgsqlParameter("claim_started_utcs", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = claims.Select(c => c.StartedUtc).ToArray()
            };

            // One statement. Each claim matches on its id and its own claim stamp, and only while still
            // Running: a request reset and reclaimed by another worker (a later started_utc), or one
            // whose outcome was saved, is not touched. attempts, next_attempt_utc and error are not set,
            // so no attempt is counted. Every value is a parameter.
            return await _db.Database.ExecuteSqlAsync($"""
                UPDATE "verification_requests" AS v
                SET "status" = {pending}, "started_utc" = NULL
                FROM unnest({ids}, {startedUtcs}) AS c("id", "started_utc")
                WHERE v."id" = c."id" AND v."started_utc" = c."started_utc" AND v."status" = {running}
                """, ct);
        }

        public Task<bool> HasOpenRequestAsync(VerificationEntityType entityType, string entityKey, CancellationToken ct = default) =>
            _db.VerificationRequests.AnyAsync(r => r.EntityType == entityType
                && r.EntityKey == entityKey
                && (r.Status == VerificationRequestStatus.Pending || r.Status == VerificationRequestStatus.Running), ct);

        public Task<VerificationRequest?> GetByIdAsync(long id, CancellationToken ct = default) =>
            _db.VerificationRequests.FirstOrDefaultAsync(r => r.Id == id, ct);

        public async Task<IReadOnlyList<VerificationRequest>> GetRecentForEntityAsync(
            VerificationEntityType entityType, string entityKey, int limit, CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

            return await _db.VerificationRequests
                .AsNoTracking()
                .Where(r => r.EntityType == entityType && r.EntityKey == entityKey)
                .OrderByDescending(r => r.RequestedUtc)
                .ThenByDescending(r => r.Id)
                .Take(limit)
                .ToListAsync(ct);
        }

        public async Task SaveChangesAsync(CancellationToken ct = default)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex) when (ex.Entries.Any(e => e.Entity is VerificationRequest))
            {
                // The xmin token: a request changed since this context read it, so the claim was
                // lost. Application can't see EF's exception type, so it gets its own.
                var requestIds = ex.Entries.Select(e => e.Entity).OfType<VerificationRequest>().Select(r => r.Id).ToList();
                throw new VerificationClaimLostException(requestIds, ex);
            }
        }

        private static bool IsOpenRequestConflict(DbUpdateException ex) =>
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
            && pg.ConstraintName == VerificationRequestConfiguration.OpenRequestIndexName;

        private static void EnsureUtc(DateTime value, string paramName)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw new ArgumentException($"{paramName} must have DateTimeKind.Utc, not {value.Kind}.", paramName);
        }
    }
}
