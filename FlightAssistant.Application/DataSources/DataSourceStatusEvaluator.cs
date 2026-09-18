namespace FlightAssistant.Application.DataSources
{
    /// <summary>
    /// Turns a stored observation into the status the page shows. Pure and static so the whole
    /// rule is table-testable without a database or a host — this is the one place the
    /// Degraded/Unhealthy line is drawn, and the admin panel renders what it is given rather than
    /// re-deriving it.
    /// </summary>
    public static class DataSourceStatusEvaluator
    {
        /// <summary>
        /// How recently a success excuses a failure. A required source that failed once but
        /// succeeded within the window is Degraded rather than Unhealthy: a single transient
        /// failure among successful calls is worth showing and not worth alarming about.
        /// </summary>
        public static readonly TimeSpan RecentSuccessWindow = TimeSpan.FromHours(1);

        public static DataSourceStatusValue Evaluate(
            DataSourceOutcome? lastOutcome,
            DateTimeOffset? lastOkUtc,
            DataSourceCriticality criticality,
            DateTimeOffset now)
        {
            if (lastOutcome is null) return DataSourceStatusValue.Unknown;

            return lastOutcome switch
            {
                DataSourceOutcome.NotConfigured => DataSourceStatusValue.NotConfigured,
                DataSourceOutcome.Ok => DataSourceStatusValue.Ok,
                DataSourceOutcome.RateLimited => DataSourceStatusValue.Degraded,
                _ when criticality == DataSourceCriticality.Optional => DataSourceStatusValue.Degraded,
                _ when HasRecentSuccess(lastOkUtc, now) => DataSourceStatusValue.Degraded,
                _ => DataSourceStatusValue.Unhealthy,
            };
        }

        private static bool HasRecentSuccess(DateTimeOffset? lastOkUtc, DateTimeOffset now) =>
            lastOkUtc is { } ok && now - ok <= RecentSuccessWindow;
    }
}
