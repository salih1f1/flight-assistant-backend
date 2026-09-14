namespace FlightAssistant.Domain.Common.Interfaces
{
    public interface IVerifiable
    {
        bool IsVerified { get; }
        DateTime? VerifiedAt { get; }
        string? VerifiedBy { get; }

        /// <summary>
        /// How the row was confirmed: <see cref="VerificationSources.Manual"/> for an admin's own
        /// click, an auto-verification provider name (or several joined with <c>+</c>) for the
        /// later steps of #355. Null while unverified.
        /// </summary>
        string? VerificationSource { get; }

        /// <summary>
        /// A short summary of why an automated check failed, e.g. "Name, City differ
        /// (ourairports)". Null unless the row is in the "needs attention" state
        /// (<see cref="IsVerified"/> false with this set) — never set by <see cref="Verify"/>.
        /// </summary>
        string? VerificationIssue { get; }

        /// <summary>Sets <see cref="IsVerified"/>, <see cref="VerifiedAt"/>, <see cref="VerifiedBy"/>
        /// and <see cref="VerificationSource"/>; clears <see cref="VerificationIssue"/>.</summary>
        void Verify(string verifiedBy, string source);

        /// <summary>Clears all five verification properties.</summary>
        void Unverify();

        /// <summary>An automated check ran and found a problem: <see cref="IsVerified"/> false,
        /// <see cref="VerifiedAt"/>/<see cref="VerifiedBy"/>/<see cref="VerificationSource"/> null,
        /// <see cref="VerificationIssue"/> set to <paramref name="issue"/>.</summary>
        void MarkVerificationFailed(string issue);
    }
}
