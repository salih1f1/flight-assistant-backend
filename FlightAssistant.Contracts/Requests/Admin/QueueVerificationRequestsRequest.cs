namespace FlightAssistant.Contracts.Requests.Admin
{
    /// <summary>Body of <c>POST /admin/verification-requests</c>: queue one check per key.</summary>
    /// <param name="EntityType">Exactly <c>Airport</c>, <c>Airline</c> or <c>Country</c>.</param>
    /// <param name="Keys">Airport and Airline ids, or Country codes; at most 1000.</param>
    /// <param name="Sources">Source names from <c>GET /admin/verification-sources</c> for the entity type.</param>
    public sealed record QueueVerificationRequestsRequest(string EntityType, IReadOnlyList<string> Keys, IReadOnlyList<string> Sources);
}
