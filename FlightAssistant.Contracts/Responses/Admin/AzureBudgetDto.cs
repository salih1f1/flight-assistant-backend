namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>
    /// The subscription's Cost Management budget and month-to-date spend, as the admin dashboard's
    /// budget card renders it (#496).
    /// </summary>
    /// <param name="State">Derived by the API, not the caller: the panel renders a state rather
    /// than inferring one from which fields are null.</param>
    /// <param name="DataAsOfUtc">Always null today (verified, not assumed — #496 task 2): neither
    /// Cost Management endpoint this reads, the Consumption Budgets list (2023-05-01) or the Cost
    /// Management query (2023-03-01), reports a last-refreshed timestamp. Kept on the contract as a
    /// hook for a future API version that might add one; null means the age is unknown, which the
    /// panel says rather than implying the figure is current.</param>
    public sealed record AzureBudgetDto(
        string State,
        string? BudgetName,
        decimal? BudgetAmount,
        decimal? CurrentSpendAmount,
        string? Currency,
        DateTimeOffset? PeriodStartUtc,
        DateTimeOffset? PeriodEndUtc,
        DateTimeOffset? DataAsOfUtc,
        string? Message);
}
