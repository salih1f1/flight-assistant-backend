namespace FlightAssistant.Application.Usage
{
    /// <param name="DataAsOfUtc">When Azure's own data was last updated, taken from its response —
    /// never the time we fetched it. Cost data lags 8-24 hours, and this panel sits beside the
    /// near-live AeroAPI gauge; stamping fetch time here would overstate freshness by up to a day.
    /// Null when the response omits it, which the panel renders as "age unknown" rather than
    /// implying the figure is current.</param>
    /// <param name="Currency">Whatever Azure reports. The subscription's billing currency is a
    /// property of the account, so this is never assumed to be USD.</param>
    /// <param name="Message">Operator-facing explanation for a non-Ok state. Sanitized to fixed
    /// categories plus facts this code controls, never a raw exception message.</param>
    public sealed record AzureBudgetSnapshot(
        AzureBudgetState State,
        string? BudgetName,
        decimal? BudgetAmount,
        decimal? CurrentSpendAmount,
        string? Currency,
        DateTimeOffset? PeriodStartUtc,
        DateTimeOffset? PeriodEndUtc,
        DateTimeOffset? DataAsOfUtc,
        string? Message)
    {
        public static AzureBudgetSnapshot NotConfigured() => new(
            AzureBudgetState.NotConfigured, null, null, null, null, null, null, null,
            "AzureCost:SubscriptionId is not set.");

        public static AzureBudgetSnapshot NotAuthorized() => new(
            AzureBudgetState.NotAuthorized, null, null, null, null, null, null, null,
            "Grant the API's managed identity the Cost Management Reader role on the subscription.");

        public static AzureBudgetSnapshot Unavailable(string message) => new(
            AzureBudgetState.Unavailable, null, null, null, null, null, null, null, message);
    }
}
