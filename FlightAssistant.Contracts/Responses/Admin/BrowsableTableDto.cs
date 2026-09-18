namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>One row of <c>GET /admin/data/tables</c>: a browsable table's slug, display name,
    /// current row count and the columns the browser exposes.</summary>
    /// <param name="Columns">In the registry's own order — the order the grid shows them in, so the
    /// filter picker and the grid agree.</param>
    public sealed record BrowsableTableDto(
        string Slug,
        string DisplayName,
        int RowCount,
        IReadOnlyList<BrowsableColumnDto> Columns);

    /// <summary>One column of a browsable table, as the panel needs it to choose a filter control
    /// (#451).</summary>
    /// <param name="Kind">The <c>BrowsableColumnKind</c> name — <c>"Text"</c>, <c>"Number"</c>,
    /// <c>"Bool"</c>, <c>"Date"</c>, <c>"Enum"</c>, <c>"Guid"</c> or <c>"Unsupported"</c> — never its
    /// number, so the panel can key off it without the enum's declaration order. <c>Unsupported</c>
    /// is a column the browser shows but cannot filter: the panel disables the control rather than
    /// offering one the API would answer with a 400.</param>
    /// <param name="Values">The names a value may take, for <c>Enum</c> alone; <c>null</c> for every
    /// other kind, which is what tells the panel to render something other than a picker.</param>
    public sealed record BrowsableColumnDto(string Name, string Kind, IReadOnlyList<string>? Values);
}
