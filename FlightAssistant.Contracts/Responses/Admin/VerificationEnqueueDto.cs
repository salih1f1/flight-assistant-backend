namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>
    /// Response of <c>POST /admin/verification-requests</c> and <c>POST /admin/verification-requests/{id}/retry</c>.
    /// </summary>
    /// <param name="Queued">New <c>Pending</c> requests.</param>
    /// <param name="Skipped">Keys not queued: a repeated key, or an entity that already has an open request —
    /// never the #418 error cooldown, which gates only the flight-tracking trigger, never an admin call.</param>
    public sealed record VerificationEnqueueDto(int Queued, int Skipped);
}
