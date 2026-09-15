using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.Persistance.Configurations.Verification;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

            var distinctKeys = keys.Distinct(StringComparer.Ordinal).ToList();

            // Built before the check so invalid arguments throw even when every key is skipped.
            var candidates = distinctKeys.Select(NewRequest).ToList();
            var skipped = keys.Count - distinctKeys.Count; // a repeated key: its first occurrence is queued or skipped

            var alreadyOpen = (await _db.VerificationRequests
                    .AsNoTracking()
                    .Where(r => r.EntityType == entityType
                        && distinctKeys.Contains(r.EntityKey)
                        && (r.Status == VerificationRequestStatus.Pending || r.Status == VerificationRequestStatus.Running))
                    .Select(r => r.EntityKey)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            var toQueue = candidates.Where(r => !alreadyOpen.Contains(r.EntityKey)).ToList();
            skipped += candidates.Count - toQueue.Count;
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

        public Task<int> ResetStuckRunningAsync(DateTime startedBeforeUtc, CancellationToken ct = default)
        {
            EnsureUtc(startedBeforeUtc, nameof(startedBeforeUtc));

            return _db.VerificationRequests
                .Where(r => r.Status == VerificationRequestStatus.Running && r.StartedUtc < startedBeforeUtc)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, VerificationRequestStatus.Pending)
                    .SetProperty(r => r.StartedUtc, (DateTime?)null), ct);
        }

        public async Task<IReadOnlyList<VerificationRequest>> ClaimBatchAsync(int batchSize, DateTime nowUtc, CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
            EnsureUtc(nowUtc, nameof(nowUtc));

            var pending = nameof(VerificationRequestStatus.Pending);
            var running = nameof(VerificationRequestStatus.Running);
            var flight = VerificationRequestConfiguration.FlightTriggerValue;

            // One statement, so selecting and marking Running cannot be separated by another
            // claimer. FOR UPDATE locks the chosen rows until this statement's transaction ends;
            // SKIP LOCKED makes a concurrent claimer pass over those rows instead of waiting for
            // them and then claiming them a second time. Every value is a parameter.
            var claimed = await _db.VerificationRequests
                .FromSql($"""
                    UPDATE "verification_requests"
                    SET "status" = {running}, "started_utc" = {nowUtc}
                    WHERE "id" IN (
                        SELECT "id"
                        FROM "verification_requests"
                        WHERE "status" = {pending}
                          AND ("next_attempt_utc" IS NULL OR "next_attempt_utc" <= {nowUtc})
                        ORDER BY ("trigger" = {flight}) DESC, "requested_utc", "id"
                        LIMIT {batchSize}
                        FOR UPDATE SKIP LOCKED)
                    RETURNING *
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

        public Task<bool> AnyOpenRequestUsesSourceAsync(string sourceName, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

            // Wrapping both sides in separators matches whole names only.
            var separator = VerificationRequest.SourceSeparator.ToString();
            var token = separator + sourceName + separator;

            return _db.VerificationRequests.AnyAsync(
                r => (r.Status == VerificationRequestStatus.Pending || r.Status == VerificationRequestStatus.Running)
                    && (separator + r.Sources + separator).Contains(token),
                ct);
        }

        public Task<VerificationRequest?> GetByIdAsync(long id, CancellationToken ct = default) =>
            _db.VerificationRequests.FirstOrDefaultAsync(r => r.Id == id, ct);

        public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);

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
