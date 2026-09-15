namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// How long a request waits before its next attempt after a source error (#367, spec §4.2,
    /// controller ruling): 1 minute after the first failed attempt, 5 minutes after the second and
    /// every later one. A source error is retryable but not necessarily transient (a reshaped
    /// upstream dataset fails the same way every time), which is why <c>MaxAttempts</c>, not the
    /// backoff, is what ends the retries.
    /// </summary>
    public static class VerificationRetryBackoff
    {
        public static readonly TimeSpan AfterFirstAttempt = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan AfterLaterAttempts = TimeSpan.FromMinutes(5);

        /// <param name="failedAttempts">The request's attempt count including the one that just
        /// failed; at least 1.</param>
        public static TimeSpan After(int failedAttempts)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
            return failedAttempts == 1 ? AfterFirstAttempt : AfterLaterAttempts;
        }
    }
}
