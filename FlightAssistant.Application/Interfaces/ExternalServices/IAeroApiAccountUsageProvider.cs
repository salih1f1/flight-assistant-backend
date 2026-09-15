namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    /// <summary>
    /// One row of the provider's own per-operation usage breakdown. <see cref="Operation"/> arrives
    /// already normalised by AeroAPI (e.g. <c>"GET /schedules/{date_start}/{date_end}"</c>), so no
    /// client-side path templating is involved anywhere in this feature.
    /// </summary>
    /// <param name="Pages">Billable unit. FlightAware bills per <b>page</b>, not per call — a
    /// multi-page search costs its page count, so <see cref="Calls"/> alone under-reports spend by
    /// the paging multiplier and must never be used to derive cost.</param>
    /// <param name="FailedCalls">Failed calls are <b>billed</b>, and are already included in
    /// <see cref="Calls"/> and <see cref="CostUsd"/>. They are reported separately for diagnosis,
    /// not to be subtracted from anything.</param>
    public sealed record AeroApiOperationUsage(
        string Operation,
        int Calls,
        int Pages,
        decimal CostUsd,
        int SuccessfulCalls,
        int FailedCalls);

    /// <summary>
    /// AeroAPI's authoritative account usage for a requested time range, as reported by the
    /// provider itself. Every figure here is upstream's — nothing is counted, priced or estimated
    /// locally, because only FlightAware knows its own per-page rates and which calls it billed.
    /// </summary>
    public sealed record AeroApiAccountUsage(
        int TotalCalls,
        int TotalPages,
        decimal TotalCostUsd,
        decimal TotalDiscountCostUsd,
        int TotalSuccessfulCalls,
        int TotalFailedCalls,
        IReadOnlyList<AeroApiOperationUsage> ByOperation);

    /// <summary>
    /// Reads account usage and spend straight from the flight data provider.
    ///
    /// <para>
    /// Defined in Application (implemented in Infrastructure) so the admin usage query never
    /// depends on the HTTP client that serves it — the same layering every other provider
    /// abstraction here follows.
    /// </para>
    /// </summary>
    public interface IAeroApiAccountUsageProvider
    {
        /// <summary>
        /// Fetches usage for <c>[startUtc, endUtc]</c>. Both bounds are UTC instants; AeroAPI caps
        /// history at one year into the past, which the query validator enforces up front so the
        /// caller gets a 400 naming the real limit rather than one relayed from upstream.
        ///
        /// <para>
        /// Throws <see cref="FlightAssistant.Domain.Common.Exceptions.DomainException"/> when the
        /// provider cannot be reached or returns an error — mapped by the same shared response
        /// handling as flight search and flight status, so the status codes cannot drift apart.
        /// </para>
        /// </summary>
        Task<AeroApiAccountUsage> GetUsageAsync(
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            CancellationToken ct = default);
    }

    /// <summary>
    /// The configured monthly spend budget, exposed here (in the Application layer) so the usage
    /// query can return it for the panel's gauge, and the budget guard can enforce it (#303), without
    /// depending on the Infrastructure-layer options class that owns it — Application never references
    /// Infrastructure. The concrete provider options type implements this interface, exactly as it does
    /// <see cref="IFlightSearchHorizonOptions"/>.
    /// </summary>
    public interface IAeroApiBudgetOptions
    {
        decimal MonthlyBudgetUsd { get; }

        /// <summary>The share of <see cref="MonthlyBudgetUsd"/> at which the budget guard's soft
        /// limit starts (#303).</summary>
        decimal SoftLimitRatio { get; }
    }
}
