namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>
    /// AeroAPI account usage and spend for a range, as reported by FlightAware itself. Every cost
    /// and count here is upstream's own figure — nothing is metered or priced locally, because
    /// billing is per page rather than per call and failed calls are billed too.
    /// </summary>
    /// <param name="MonthlyBudgetUsd">Local configuration (<c>AeroApi:MonthlyBudgetUsd</c>), not an
    /// upstream figure — the reference line the panel's gauge is drawn against.</param>
    /// <param name="NextSlotUtc">When the next outbound AeroAPI call is due on the shared outbound
    /// throttle, or null if none has been reserved yet. Live state, never cached.</param>
    public sealed record AeroApiUsageDto(
        DateTimeOffset RangeStartUtc,
        DateTimeOffset RangeEndUtc,
        int TotalCalls,
        int TotalPages,
        decimal TotalCostUsd,
        decimal TotalDiscountCostUsd,
        int TotalSuccessfulCalls,
        int TotalFailedCalls,
        decimal MonthlyBudgetUsd,
        IReadOnlyList<AeroApiUsageOperationDto> ByOperation,
        DateTimeOffset? NextSlotUtc
    );

    /// <summary>
    /// One operation's slice of the reported spend. <paramref name="Operation"/> is AeroAPI's own
    /// normalised form, e.g. <c>GET /schedules/{date_start}/{date_end}</c>.
    /// </summary>
    /// <param name="Pages">The billable unit — a paginated search costs its page count, not one.</param>
    /// <param name="FailedCalls">Billed, and already included in <paramref name="Calls"/> and
    /// <paramref name="CostUsd"/>.</param>
    public sealed record AeroApiUsageOperationDto(
        string Operation,
        int Calls,
        int Pages,
        decimal CostUsd,
        int SuccessfulCalls,
        int FailedCalls
    );
}
