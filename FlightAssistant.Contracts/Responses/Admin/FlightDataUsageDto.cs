namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>One operation's slice of the reported AeroAPI spend.</summary>
    public sealed record FlightDataSpendOperationDto(
        string Operation,
        int Calls,
        int Pages,
        decimal CostUsd);

    /// <summary>Tracked flights in one sync mode. <c>Mode</c> is null for a flight never planned.</summary>
    public sealed record FlightDataSyncModeCountDto(string? Mode, int Flights);

    /// <summary>What the window's polls produced, per recording source.</summary>
    public sealed record FlightDataEventYieldDto(string Source, int Events, int DistinctFlights);

    /// <summary>
    /// Calculated, not measured. AeroAPI exposes no per-request identifier and bills per page
    /// (failed calls included), so no figure here attributes a page to a flight — these are
    /// whole-window spend over a whole-window denominator. Null when the denominator is zero.
    /// </summary>
    public sealed record FlightDataDerivedDto(
        decimal? CostUsdPerActiveTrackedFlight,
        decimal? CostUsdPerRecordedEvent);

    /// <summary>
    /// AeroAPI spend for the window paired with our own sync activity over it (#503), so a
    /// #364 Phase 4 decision can be made from numbers rather than guesses.
    /// </summary>
    public sealed record FlightDataUsageDto(
        DateTimeOffset RangeStartUtc,
        DateTimeOffset RangeEndUtc,
        int TotalCalls,
        int TotalPages,
        decimal TotalCostUsd,
        decimal MonthlyBudgetUsd,
        IReadOnlyList<FlightDataSpendOperationDto> ByOperation,
        int ActiveTrackedFlights,
        IReadOnlyList<FlightDataSyncModeCountDto> ByStoredSyncMode,
        int TotalEventsRecorded,
        IReadOnlyList<FlightDataEventYieldDto> EventYield,
        FlightDataDerivedDto Derived);
}
