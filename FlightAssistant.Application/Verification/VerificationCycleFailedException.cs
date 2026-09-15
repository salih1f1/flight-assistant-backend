namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// A verification cycle that failed only after it had worked through its batch; today that means
    /// releasing unused source caches threw. The cycle still failed, but <see cref="CompletedWork"/>
    /// says what it did, so the job's heartbeat reports this cycle's counts instead of keeping the
    /// previous cycle's. The cause is the <see cref="Exception.InnerException"/>.
    /// </summary>
    public sealed class VerificationCycleFailedException : Exception
    {
        public VerificationCycleFailedException(VerificationCycleResult completedWork, Exception innerException)
            : base("The verification cycle failed after processing its batch.", innerException)
        {
            ArgumentNullException.ThrowIfNull(completedWork);
            ArgumentNullException.ThrowIfNull(innerException);
            CompletedWork = completedWork;
        }

        /// <summary>What the cycle did before it failed.</summary>
        public VerificationCycleResult CompletedWork { get; }
    }
}
