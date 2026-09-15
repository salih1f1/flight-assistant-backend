namespace FlightAssistant.Domain.Verification.Enums
{
    /// <summary>
    /// Where a <see cref="Entities.VerificationRequest"/> is in its lifecycle. Stored by name in
    /// <c>verification_requests.status</c>, and the open-request unique index filters on the names
    /// <c>Pending</c> and <c>Running</c>, so renaming a member is a data and index migration.
    /// </summary>
    public enum VerificationRequestStatus
    {
        /// <summary>Waiting to be claimed; open.</summary>
        Pending,

        /// <summary>Claimed by the job; open.</summary>
        Running,

        /// <summary>Every selected source matched. Completed.</summary>
        Verified,

        /// <summary>At least one source disagreed on a checked column. Completed.</summary>
        Mismatch,

        /// <summary>At least one source had no row for the entity. Completed.</summary>
        NotFound,

        /// <summary>The check could not be completed (retries used up, entity gone, unknown source). Completed.</summary>
        Error
    }
}
