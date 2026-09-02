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
        /// method trusts them and does not re-check, so an unvalidated caller is a bug, not a
        /// runtime guard here.
        /// </summary>
        /// <param name="sortColumn">
        /// A column name, or <c>null</c> for no caller-requested sort. Every ordering, including
        /// the no-sort case, still carries a deterministic primary-key tiebreaker — several
        /// browsable tables have no naturally unique column otherwise, and without a tiebreaker a
        /// row can appear on two pages or none.
        /// </param>
        /// <param name="filterColumn">
        /// A column name, or <c>null</c> for no filter. Exact match for a non-string column;
        /// case-insensitive <c>contains</c> for a string column, translated to SQL rather than
        /// evaluated client-side.
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
    }
}
