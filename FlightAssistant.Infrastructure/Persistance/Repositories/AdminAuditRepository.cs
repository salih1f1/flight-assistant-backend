using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class AdminAuditRepository : IAdminAuditRepository
    {
        private readonly FlightAssistantDbContext _db;
        public AdminAuditRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<(IReadOnlyList<AdminAuditLog> Items, int Total)> SearchAsync(
            int page,
            int pageSize,
            string? entityType,
            string? entityId,
            string? actorId,
            AdminAuditAction? action,
            DateTime? fromUtc,
            DateTime? toUtc,
            CancellationToken ct = default)
        {
            var query = _db.AdminAuditLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(entityType))
                query = query.Where(a => a.EntityType == entityType);
            if (!string.IsNullOrWhiteSpace(entityId))
                query = query.Where(a => a.EntityId == entityId);
            if (!string.IsNullOrWhiteSpace(actorId))
            {
                // Case-insensitive on purpose (#313 final review, M3) — ActorId is an oid claim
                // (a GUID string), and its casing varies by source (Entra emits lowercase, some
                // tooling upper-cases). A case-mismatched exact match returns an empty page, which
                // on an audit tool reads as "this admin did nothing" — the worst failure mode for
                // the question the tool exists to answer. EntityType stays exact-match: it is a
                // CLR type name (generated, not typed by a human off some other source), so the
                // casing-varies-by-source risk that justifies this for ActorId doesn't apply.
                // ToLower() on both sides (not ILike, which only Postgres understands and would
                // silently stop translating on the InMemory provider the API test suite runs on).
                var actorIdLower = actorId.ToLower();
                query = query.Where(a => a.ActorId.ToLower() == actorIdLower);
            }
            if (action.HasValue)
                query = query.Where(a => a.Action == action.Value);
            if (fromUtc.HasValue)
                query = query.Where(a => a.ActionUtc >= ToUtcKind(fromUtc.Value));
            if (toUtc.HasValue)
                query = query.Where(a => a.ActionUtc <= ToUtcKind(toUtc.Value));

            // Total must be counted after every filter above, before paging — this is the classic
            // bug in this shape (a Total computed against the unfiltered table).
            var total = await query.CountAsync(ct);

            // ThenByDescending(Id) is a deterministic tiebreaker (#313 final review, M5):
            // AdminAuditInterceptor.WriteAuditRows stamps one ActionUtc instant on every row
            // written in a single save, so a large batch (e.g. a CSV import) is one large tie on
            // ActionUtc alone. Skip/Take over an unordered tie group is not guaranteed stable
            // between the page-1 and page-2 queries, which could duplicate a row across pages or
            // drop one — Id is unique, so adding it as a secondary sort key makes the order total.
            var items = await query
                .OrderByDescending(a => a.ActionUtc)
                .ThenByDescending(a => a.Id)
                .Page(page, pageSize)
                .ToListAsync(ct);

            return (items, total);
        }

        public Task AddAsync(AdminAuditLog entry, CancellationToken ct = default)
        {
            _db.AdminAuditLogs.Add(entry);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);

        /// <summary>
        /// Defense-in-depth against #313 final review C1: <c>ActionUtc</c> is <c>timestamptz</c>,
        /// and Npgsql throws <see cref="ArgumentException"/> writing anything but
        /// <see cref="DateTimeKind.Utc"/> against it. <c>AdminAuditEndpoints</c> already guarantees
        /// a Utc-kind value by binding <c>DateTimeOffset?</c> at the API boundary, but this
        /// repository has exactly one public entry point and no way to enforce that guarantee on
        /// every future caller (a different endpoint, an admin CLI, a background job) — normalising
        /// here means an Unspecified or Local value can never reach Npgsql and blow up the query,
        /// regardless of what called this method. An Unspecified value is treated as already UTC
        /// (matching the endpoint's own contract: parameters are named fromUtc/toUtc, so a naive
        /// value is the caller asserting it already is UTC), never re-interpreted through the
        /// process's local time zone.
        /// </summary>
        private static DateTime ToUtcKind(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
