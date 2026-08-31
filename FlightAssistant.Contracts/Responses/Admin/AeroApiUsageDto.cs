namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <param name="EstimatedSpendUsd">
    /// <b>Upper bound on spend.</b> Every call in the range priced by endpoint template, including
    /// throttled calls (a 429 is refused before FlightAware serves it and is not a billed query)
    /// and every failed retry attempt. Compare against <paramref name="BillableSpendUsd"/>.
    /// </param>
    /// <param name="BillableSpendUsd">
    /// <b>Lower bound on spend.</b> The same figure with throttled calls excluded. Failed calls are
    /// still included, because a 4xx is plausibly billed and a 5xx plausibly is not, and only the
    /// invoice can settle it — so the pair brackets the real number rather than guessing at it.
    /// </param>
    /// <param name="TransportFailureCountLast7Days">
    /// Calls in the last 7 days that got no response at all (unreachable, timed out, unparseable) —
    /// the failures this API maps to a 502 for its own clients. AeroAPI never returns a 502 itself.
    /// </param>
    public sealed record AeroApiUsageDto(
        DateTime FromUtc,
        DateTime ToUtc,
        int TotalCalls,
        decimal EstimatedSpendUsd,
        decimal BillableSpendUsd,
        decimal MonthlyBudgetUsd,
        int CurrentMonthCalls,
        decimal CurrentMonthSpendUsd,
        IReadOnlyList<AeroApiUsageTemplateBreakdownDto> ByTemplate,
        IReadOnlyList<AeroApiUsageOutcomeBreakdownDto> ByOutcome,
        IReadOnlyList<AeroApiUsageDailyCountDto> DailyCounts,
        int Status429CountLast7Days,
        int TransportFailureCountLast7Days,
        DateTimeOffset? NextSlotUtc
    );

    public sealed record AeroApiUsageTemplateBreakdownDto(
        string EndpointTemplate,
        int Calls,
        decimal SpendUsd,
        decimal Share
    );

    public sealed record AeroApiUsageOutcomeBreakdownDto(string Outcome, int Calls);

    public sealed record AeroApiUsageDailyCountDto(DateOnly Date, int Calls);
}
