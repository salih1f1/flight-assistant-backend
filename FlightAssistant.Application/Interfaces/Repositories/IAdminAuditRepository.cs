using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IAdminAuditRepository
    {
        /// <summary>
        /// Newest-first by <c>ActionUtc</c> (matches the index Task 1 created), then by <c>Id</c>
        /// descending as a deterministic tiebreaker — rows written in the same admin save share one
        /// <c>ActionUtc</c> instant, so <c>ActionUtc</c> alone does not make paging stable over a
        /// large batch. All filters are optional and combine with AND. <paramref name="actorId"/>
        /// matches case-insensitively (an <c>oid</c> claim's casing varies by source); every other
        /// string filter is an exact match. <paramref name="total"/> in the returned tuple reflects
        /// the count after every filter is applied, not the table count. <paramref name="fromUtc"/>
        /// and <paramref name="toUtc"/> are both inclusive bounds and must already carry
        /// <see cref="DateTimeKind.Utc"/> — Npgsql refuses to write anything else against the
        /// underlying <c>timestamptz</c> column. <c>AdminAuditEndpoints</c> guarantees this by
        /// binding <c>DateTimeOffset?</c> at the API boundary and projecting through
        /// <c>.UtcDateTime</c> before a value ever reaches <c>GetAdminAuditQuery</c>; this method
        /// does not normalise <c>Kind</c> itself.
        /// </summary>
        Task<(IReadOnlyList<AdminAuditLog> Items, int Total)> SearchAsync(
            int page,
            int pageSize,
            string? entityType,
            string? entityId,
            string? actorId,
            AdminAuditAction? action,
            DateTime? fromUtc,
            DateTime? toUtc,
            CancellationToken ct = default);

        /// <summary>
        /// Stages <paramref name="entry"/> on the change tracker. Does not save — matches every
        /// other repository's Add/Delete-then-explicit-<see cref="SaveChangesAsync"/> split in this
        /// codebase (e.g. <c>IAdminTravelerRepository.DeleteAsync</c>), so a caller controls exactly
        /// when the write commits.
        ///
        /// <para>
        /// This exists only for a caller with nothing else on the change tracker to audit — the
        /// audited reveal (#318), which is a read and so gives the <c>AdminAuditInterceptor</c>
        /// (Infrastructure) nothing to observe on its own. Every ordinary admin write is still
        /// audited automatically by that interceptor; calling this alongside a tracked
        /// create/update/delete in the same <see cref="SaveChangesAsync"/> would double-audit that
        /// write, so no other caller should reach for this.
        /// </para>
        /// </summary>
        Task AddAsync(AdminAuditLog entry, CancellationToken ct = default);

        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
