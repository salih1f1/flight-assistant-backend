namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>One row of <c>GET /admin/data/tables</c>: a browsable table's slug, display name
    /// and current row count.</summary>
    public sealed record BrowsableTableDto(string Slug, string DisplayName, int RowCount);
}
