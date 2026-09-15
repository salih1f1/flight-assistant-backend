namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>One row of <c>GET /admin/verification-sources</c>: a source an admin can pick, and what one row lookup against it costs.</summary>
    public sealed record VerificationSourceDto(string Name, decimal CostPerRowUsd);
}
