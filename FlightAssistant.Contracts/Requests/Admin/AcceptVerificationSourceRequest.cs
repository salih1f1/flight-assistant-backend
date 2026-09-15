namespace FlightAssistant.Contracts.Requests.Admin
{
    /// <summary>Body of <c>POST /admin/verification-requests/{id}/accept-source</c>: which of the request's sources to accept.</summary>
    public sealed record AcceptVerificationSourceRequest(string Source);
}
