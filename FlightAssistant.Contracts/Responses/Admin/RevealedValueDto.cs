namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>
    /// Response of <c>POST /admin/data/{table}/{id}/reveal</c> (#318): the table, row and column
    /// identifying the value, plus the full unmasked value itself. Nothing else — no audit-row
    /// details, no surrounding row data.
    /// </summary>
    public sealed record RevealedValueDto(string Table, string Id, string Column, object? Value);
}
