namespace FlightAssistant.Domain.Verification.Enums
{
    /// <summary>
    /// The reference-data types an automated check can target. Stored by name in
    /// <c>verification_requests.entity_type</c>, so renaming a member is a data migration.
    /// </summary>
    public enum VerificationEntityType
    {
        Airport,
        Airline,
        Country
    }
}
