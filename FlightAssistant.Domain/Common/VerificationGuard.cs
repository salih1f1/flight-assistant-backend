namespace FlightAssistant.Domain.Common
{
    /// <summary>
    /// The argument rules every <see cref="Interfaces.IVerifiable"/> implementation enforces before
    /// it changes any verification state (#367, deferred from #355). Kept in one place so the five
    /// implementations (<see cref="SeedableEntity"/>, <c>Lounge</c>, <c>LoungeProvider</c>,
    /// <c>LoyaltyProgram</c>, <c>LoyaltyProgramKind</c>) cannot drift apart on them.
    ///
    /// <para>
    /// The length limits are the column sizes every verifiable entity's configuration maps
    /// (<c>verified_by varchar(255)</c>, <c>verification_source varchar(100)</c>,
    /// <c>verification_issue varchar(500)</c>). Rejecting an over-long value here turns what used to
    /// be a database error at <c>SaveChanges</c> into an <see cref="ArgumentException"/> at the call
    /// that caused it.
    /// </para>
    /// </summary>
    public static class VerificationGuard
    {
        /// <summary>Column size of <c>verified_by</c>.</summary>
        public const int MaxVerifiedByLength = 255;

        /// <summary>Column size of <c>verification_source</c>.</summary>
        public const int MaxSourceLength = 100;

        /// <summary>Column size of <c>verification_issue</c>.</summary>
        public const int MaxIssueLength = 500;

        /// <summary>
        /// Rules for <c>Verify(verifiedBy, source, verifiedAtUtc)</c>: <paramref name="verifiedBy"/>
        /// and <paramref name="source"/> are non-blank and fit their columns, and
        /// <paramref name="verifiedAtUtc"/> has <see cref="DateTimeKind.Utc"/>, since Npgsql refuses
        /// any other kind against a <c>timestamptz</c> column.
        /// </summary>
        /// <exception cref="ArgumentException">A rule is broken. <see cref="ArgumentException.ParamName"/> names the argument.</exception>
        public static void EnsureValidVerify(string verifiedBy, string source, DateTime verifiedAtUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(verifiedBy);
            ArgumentException.ThrowIfNullOrWhiteSpace(source);

            if (verifiedBy.Length > MaxVerifiedByLength)
                throw new ArgumentException(
                    $"verifiedBy is {verifiedBy.Length} characters; the limit is {MaxVerifiedByLength}.",
                    nameof(verifiedBy));

            if (source.Length > MaxSourceLength)
                throw new ArgumentException(
                    $"source is {source.Length} characters; the limit is {MaxSourceLength}.",
                    nameof(source));

            if (verifiedAtUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException(
                    $"verifiedAtUtc must have DateTimeKind.Utc, not {verifiedAtUtc.Kind}.",
                    nameof(verifiedAtUtc));
        }

        /// <summary>Rules for <c>MarkVerificationFailed(issue)</c>: <paramref name="issue"/> is
        /// non-blank and at most <see cref="MaxIssueLength"/> characters.</summary>
        /// <exception cref="ArgumentException">A rule is broken.</exception>
        public static void EnsureValidIssue(string issue)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(issue);

            if (issue.Length > MaxIssueLength)
                throw new ArgumentException(
                    $"issue is {issue.Length} characters; the limit is {MaxIssueLength}.",
                    nameof(issue));
        }
    }
}
