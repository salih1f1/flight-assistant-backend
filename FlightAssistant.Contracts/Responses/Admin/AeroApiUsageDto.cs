namespace FlightAssistant.Contracts.Responses.Admin
{
    public sealed record AeroApiUsageDto(
        DateTime FromUtc,
        DateTime ToUtc,
        int TotalCalls,
        decimal EstimatedSpendUsd,
        decimal MonthlyBudgetUsd,
        int CurrentMonthCalls,
        decimal CurrentMonthSpendUsd,
        IReadOnlyList<AeroApiUsageTemplateBreakdownDto> ByTemplate,
        IReadOnlyList<AeroApiUsageOutcomeBreakdownDto> ByOutcome,
        IReadOnlyList<AeroApiUsageDailyCountDto> DailyCounts,
        int Status429CountLast7Days,
        int Status502CountLast7Days,
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
