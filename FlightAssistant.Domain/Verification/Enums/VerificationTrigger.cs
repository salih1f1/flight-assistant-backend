namespace FlightAssistant.Domain.Verification.Enums
{
    /// <summary>
    /// What queued a <see cref="Entities.VerificationRequest"/>. Stored lower-case in
    /// <c>verification_requests.trigger</c> (<c>admin</c>, <c>flight</c>); the job claims
    /// <see cref="Flight"/> requests first.
    /// </summary>
    public enum VerificationTrigger
    {
        /// <summary>An admin asked for the check.</summary>
        Admin,

        /// <summary>A traveler tracked a flight that uses the entity.</summary>
        Flight
    }
}
