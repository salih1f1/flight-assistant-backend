namespace FlightAssistant.Domain.Verification.Enums
{
    /// <summary>
    /// What queued a <see cref="Entities.VerificationRequest"/>. Stored lower-case in
    /// <c>verification_requests.trigger</c> (<c>admin</c>, <c>flight</c>); the job claims
    /// <see cref="Flight"/> requests first, until an older request of any trigger reaches
    /// <c>Verification:ClaimPromotionAge</c> (#419) and ranks alongside them instead. See
    /// <c>IVerificationRequestRepository.ClaimBatchAsync</c>.
    /// </summary>
    public enum VerificationTrigger
    {
        /// <summary>An admin asked for the check.</summary>
        Admin,

        /// <summary>A traveler tracked a flight that uses the entity.</summary>
        Flight
    }
}
