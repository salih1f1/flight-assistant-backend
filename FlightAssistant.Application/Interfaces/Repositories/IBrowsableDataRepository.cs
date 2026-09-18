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
        /// <paramref name="table"/> exposes. <paramref name="sortColumn"/> and every column in
        /// <paramref name="filters"/> must already be validated against
        /// <c>BrowsableTables.IsColumnExposed</c> and confirmed non-sensitive by the caller — this
        /// method does not repeat that allowlist/sensitivity check itself, so an unvalidated caller
        /// is a bug, not something this method guards against. Column-name resolution safety is
        /// asymmetric between the two, though neither ever lets an unknown name become part of raw
        /// SQL: see <paramref name="sortColumn"/> and <paramref name="filters"/> below.
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
        /// <param name="filters">
        /// Zero or more <c>(Column, Value)</c> criteria, <b>combined with AND</b> — every criterion
        /// must match for a row to be returned (#476). An empty list is "no filter". Repeats of the
        /// same column are legitimate and AND like any other pair. Each value is the raw string as
        /// received from the client: exact match for a non-string column, case-insensitive
        /// <c>contains</c> for a string column, translated to SQL rather than evaluated
        /// client-side, and converted internally to the column's real CLR type — a value that will
        /// not parse throws a 400 <c>DomainException</c> rather than letting an unhandled parse
        /// exception surface as a 500. Unlike <paramref name="sortColumn"/>, each column <em>is</em>
        /// re-checked explicitly via reflection and throws <see cref="InvalidOperationException"/>
        /// for a name that doesn't exist on the entity — defense-in-depth alongside the caller's
        /// own allowlist check, not a substitute for it.
        /// </param>
        Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Items, int Total)> QueryAsync(
            BrowsableTable table,
            int page,
            int pageSize,
            string? sortColumn,
            bool sortDescending,
            IReadOnlyList<(string Column, string Value)> filters,
            CancellationToken ct = default);

        /// <summary>
        /// The single, unmasked value of <paramref name="column"/> on the one row of
        /// <paramref name="table"/> whose primary key stringifies to <paramref name="id"/> — backs
        /// the audited reveal (#318). Deliberately narrower than a whole-row read: the reveal
        /// handler only ever wants exactly one already-confirmed-sensitive column, so this
        /// signature makes wholesale exposure of every exposed column structurally impossible
        /// rather than documentation-enforced (#318 final review, finding 4 — this replaced a prior
        /// <c>GetRowAsync</c> that returned the whole projected row). <paramref name="column"/>
        /// must already be confirmed both exposed (<c>BrowsableTables.IsColumnExposed</c>) and
        /// sensitive (<c>SensitiveProperties.IsSensitive</c>) by the caller — this method does not
        /// repeat that check itself, matching <see cref="QueryAsync"/>'s <c>filters</c>
        /// contract; a name that doesn't exist on the entity at all still throws
        /// <see cref="InvalidOperationException"/> defensively.
        /// </summary>
        /// <param name="id">
        /// The primary key, stringified exactly as <c>AdminAuditInterceptor.GetEntityId</c> would
        /// format it — a single key's value as-is, or a composite key's parts joined with
        /// <c>|</c> in the order EF's own metadata reports them. Resolved via that same EF
        /// metadata (<c>IEntityType.FindPrimaryKey()</c>), not an assumption that the key property
        /// is called "Id" — several browsable entities key on something else entirely. A value
        /// that does not split into the right number of parts, or a part that fails to parse as
        /// its key column's CLR type — including a <c>string</c>-typed key column
        /// (<c>Country.Code</c>), which parses as itself (#318 final review, finding 1) — is
        /// treated as "no such row" (a <c>null</c> return) rather than a thrown exception,
        /// resolving to 404, never a 500, for what is ultimately just an unrecognised id. The match
        /// is always exact, for every key type including string — the case-insensitive
        /// <em>contains</em> semantics <see cref="QueryAsync"/>'s <c>filters</c> give a string
        /// column do not apply here; a prefix must never resolve a different row's id.
        /// </param>
        /// <returns>
        /// The resolved row's canonical id (see <see cref="RevealedValue.EntityId"/>) and the raw
        /// <paramref name="column"/> value, or <c>null</c> when no row has that id.
        /// </returns>
        Task<RevealedValue?> GetValueAsync(
            BrowsableTable table,
            string id,
            string column,
            CancellationToken ct = default);
    }

    /// <summary>
    /// The result of <see cref="IBrowsableDataRepository.GetValueAsync"/>.
    /// </summary>
    /// <param name="EntityId">
    /// The resolved row's primary key, stringified from the same EF key metadata
    /// <c>AdminAuditInterceptor.GetEntityId</c> reads (<c>IEntityType.FindPrimaryKey()</c>, each
    /// part's <c>.ToString()</c>, joined with <c>|</c> for a composite key) — never the caller's
    /// raw <c>id</c> argument echoed back. A caller's id may resolve the right row while carrying a
    /// non-canonical spelling (a braced/uppercase Guid, for instance); deriving this from the
    /// loaded row's own key instead means an audit row's <c>EntityId</c> always matches the format
    /// every other admin-write audit row already uses, by construction rather than by a
    /// caller-side normalisation step that could drift from the interceptor's own (#318 final
    /// review, finding 2).
    /// </param>
    /// <param name="Value">The raw, unmasked value of the requested column.</param>
    public sealed record RevealedValue(string EntityId, object? Value);
}
