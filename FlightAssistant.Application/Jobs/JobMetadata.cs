namespace FlightAssistant.Application.Jobs
{
    /// <summary>
    /// The two small per-job facts <c>GetJobsQueryHandler</c> and <c>ToggleJobCommandHandler</c> both
    /// need to build an <c>AdminJobDto</c>: which config key gates the job, and what to call it. Kept
    /// beside <see cref="JobNames"/> rather than folded into it — <see cref="JobNames"/> (#471 Task 1)
    /// is deliberately just the closed name list, and this is presentation/config metadata, not
    /// identity.
    /// </summary>
    internal static class JobMetadata
    {
        /// <summary>The same config key, with the same <c>true</c> default, that the owning hosted
        /// service itself reads (<c>FlightStatusSyncBackgroundService</c> /
        /// <c>ReferenceVerificationBackgroundService</c>) — so this ceiling can never disagree with
        /// what the loop actually obeys.</summary>
        public static string ConfigKeyFor(string job) => job switch
        {
            JobNames.FlightSync => "FlightSync:Enabled",
            JobNames.Verification => "Verification:Enabled",
            _ => throw Unknown(job)
        };

        public static string DisplayNameFor(string job) => job switch
        {
            JobNames.FlightSync => "Flight status sync",
            JobNames.Verification => "Reference verification",
            _ => throw Unknown(job)
        };

        // Both callers validate the job name against JobNames.All before reaching either method above
        // (ToggleJobCommandHandler's own 404 check; GetJobsQueryHandler only ever iterates
        // JobNames.All itself) — this is a defensive "should never happen", not a second source of
        // the 404 an API caller sees.
        private static InvalidOperationException Unknown(string job) =>
            new($"'{job}' is not a known job name.");
    }
}
