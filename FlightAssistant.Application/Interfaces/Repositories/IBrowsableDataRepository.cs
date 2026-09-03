using FlightAssistant.Application.Administration;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    /// <summary>
    /// The dynamic read path behind the admin data browser (#315 Task 2). One implementation reads
    /// any <see cref="BrowsableTable"/> registered in <c>BrowsableTables</c> — the handler never
    /// touches <c>DbContext</c> directly, matching the split <c>IAdminAuditRepository</c> already
    /// uses (Application defines the contract, Infrastructure builds the query).
    ///
    /// <para>
    /// Every method here is strictly read-only (<c>AsNoTracking</c> throughout on the
    /// implementation) and returns raw, <b>unmasked</b> column values — masking
    /// (<c>SensitiveProperties.IsSensitive</c>) is applied by the query handler, not here, so this
    /// interface has no reason to know about redaction at all.
    /// </para>
    /// </summary>
    public interface IBrowsableDataRepository
    {
        /// <summary>Total row count for <paramref name="table"/>, unfiltered — backs
        /// <c>GET /admin/data/tables</c>.</summary>
        Task<int> CountAsync(BrowsableTable table, CancellationToken ct = default);

        /// <summary>
        /// Paginated rows for <paramref name="table"/>, projected to only the columns
        /// <paramref name="table"/> exposes. <paramref name="sortColumn"/> and
        /// <paramref name="filterColumn"/> must already be validated against
        /// <c>BrowsableTables.IsColumnExposed</c> and confirmed non-sensitive by the caller — this
        /// method does not repeat that allowlist/sensitivity check itself, so an unvalidated caller
        /// is a bug, not something this method guards against. Column-name resolution safety is
        /// asymmetric between the two, though neither ever lets an unknown name become part of raw
        /// SQL: see <paramref name="sortColumn"/> and <paramref name="filterColumn"/> below.
        /// </summary>
        /// <param name="sortColumn">
        /// A column name, or <c>null</c> for no caller-requested sort. Every ordering, including
        /// the no-sort case, still carries a deterministic primary-key tiebreaker — several
        /// browsable tables have no naturally unique column otherwise, and without a tiebreaker a
        /// row can appear on two pages or none. Not re-checked against the entity by this method —
        /// an unknown name surfaces as an <see cref="InvalidOperationException"/> thrown by EF
        /// Core's own translation of <c>EF.Property</c> against model metadata, not from an explicit
        /// guard here.
        /// </param>
        /// <param name="filterColumn">
        /// A column name, or <c>null</c> for no filter. Exact match for a non-string column;
        /// case-insensitive <c>contains</c> for a string column, translated to SQL rather than
        /// evaluated client-side. Unlike <paramref name="sortColumn"/>, this one <em>is</em>
        /// re-checked explicitly via reflection and throws <see cref="InvalidOperationException"/>
        /// for a name that doesn't exist on the entity — defense-in-depth alongside the caller's
        /// own allowlist check, not a substitute for it.
        /// </param>
        /// <param name="filterValue">
        /// The raw filter value as received from the client. Ignored when
        /// <paramref name="filterColumn"/> is <c>null</c>. Converted internally to the column's real
        /// CLR type; a value that will not parse throws a 400 <c>DomainException</c> rather than
        /// letting an unhandled parse exception surface as a 500.
        /// </param>
        Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Items, int Total)> QueryAsync(
            BrowsableTable table,
            int page,
            int pageSize,
            string? sortColumn,
            bool sortDescending,
            string? filterColumn,
            string? filterValue,
            CancellationToken ct = default);

        /// <summary>
        /// The single row of <paramref name="table"/> whose primary key stringifies to
        /// <paramref name="id"/>, projected to only the columns <paramref name="table"/> exposes —
        /// same projection <see cref="QueryAsync"/> uses, but keyed instead of paged. Backs the
        /// audited reveal (#318): unlike every other read on this interface, the values returned
        /// here are <b>never</b> masked by this method or any other reader upstream of the reveal
        /// handler, so a caller must not expose this row wholesale — only the one column the
        /// caller has already confirmed is both exposed and registered in
        /// <c>SensitiveProperties</c> may ever leave the handler.
        /// </summary>
        /// <param name="id">
        /// The primary key, stringified exactly as <c>AdminAuditInterceptor.GetEntityId</c> would
        /// format it — a single key's value as-is, or a composite key's parts joined with
        /// <c>|</c> in the order EF's own metadata reports them. Resolved via that same EF
        /// metadata (<c>IEntityType.FindPrimaryKey()</c>), not an assumption that the key property
        /// is called "Id" — several browsable entities key on something else entirely. A value
        /// that does not split into the right number of parts, or a part that fails to parse as
        /// its key column's CLR type, is treated as "no such row" (a <c>null</c> return) rather
        /// than a thrown exception — resolving to 404, never a 500, for what is ultimately just an
        /// unrecognised id.
        /// </param>
        /// <returns>
        /// The row's exposed columns, unmasked, or <c>null</c> when no row has that id.
        /// </returns>
        Task<IReadOnlyDictionary<string, object?>?> GetRowAsync(
            BrowsableTable table,
            string id,
            CancellationToken ct = default);
    }
}
