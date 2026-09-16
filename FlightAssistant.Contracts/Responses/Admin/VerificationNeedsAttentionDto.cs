namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>
    /// One row of <c>GET /admin/verification-needs-attention</c> (#385, Task 2): a
    /// <c>VerificationEntityType</c> (its enum member name, e.g. <c>"Airport"</c>) and how many of its
    /// rows need attention -- <c>IsVerified</c> false and a non-blank <c>VerificationIssue</c>.
    /// </summary>
    public sealed record VerificationNeedsAttentionDto(string EntityType, int Count);
}
