namespace FlightAssistant.Contracts.Requests.Admin
{
    /// <summary>Body of <c>POST /admin/data/{table}/{id}/reveal</c>: which column to reveal.</summary>
    public sealed record RevealValueRequest(string Column);
}
