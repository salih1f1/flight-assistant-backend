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

        public VerificationRequestRepository(FlightAssistantDbContext db, TimeProvider timeProvider)
        {
            _db = db;
            _timeProvider = timeProvider;
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
            var reset = await _db.VerificationRequests
                .FromSql($"""
                    UPDATE "verification_requests" AS v
                    SET "attempts" = v."attempts" + 1,
                        "status" = CASE WHEN v."attempts" + 1 >= {maxAttempts} THEN {error} ELSE {pending} END,
                        "error" = CASE WHEN v."attempts" + 1 >= {maxAttempts}
                            THEN {abandonedPrefix} || (v."attempts" + 1) || {abandonedSuffix}
                            ELSE v."error" END,
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
            int batchSize, DateTime nowUtc, IReadOnlyCollection<long>? excludeIds = null, CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
            EnsureUtc(nowUtc, nameof(nowUtc));

            var pending = nameof(VerificationRequestStatus.Pending);
            var running = nameof(VerificationRequestStatus.Running);
            var flight = VerificationRequestConfiguration.FlightTriggerValue;

            // Always a concrete array, never null: ANY(NULL::bigint[]) is NULL, not false, which
            // would make "NOT (id = ANY(...))" NULL too and silently exclude every row.
            var exclude = excludeIds?.ToArray() ?? Array.Empty<long>();

            // One statement, so choosing rows and marking them Running cannot be separated by another
            // claimer. The CTE picks at most batchSize ids exactly once; an IN (subquery) form can be
            // rescanned by the planner and claim more. FOR UPDATE locks the chosen rows until this
            // statement's transaction ends; SKIP LOCKED makes a concurrent claimer pass over them
            // instead of waiting and then claiming them a second time. xmin (the concurrency token)
            // is returned explicitly because RETURNING v.* never includes system columns. Every
            // value is a parameter.
            var claimed = await _db.VerificationRequests
                .FromSql($"""
                    WITH claimable AS (
                        SELECT "id"
                        FROM "verification_requests"
                        WHERE "status" = {pending}
                          AND ("next_attempt_utc" IS NULL OR "next_attempt_utc" <= {nowUtc})
                          AND NOT ("id" = ANY({exclude}))
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
