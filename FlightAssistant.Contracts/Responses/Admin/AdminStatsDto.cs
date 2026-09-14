namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>
    /// The admin dashboard cards' numbers for one period (#336). The window is whole buckets ending
    /// with the current one, so each card's <c>NewInPeriod</c> equals the sum of its <c>Series</c>.
    /// </summary>
    /// <param name="FromUtc">Inclusive start of the window, on an hour or UTC-day boundary. Filtering
    /// a card's data browser table on <c>{DateColumn}:&gt;={FromUtc}</c> lists its new rows.</param>
    /// <param name="ToUtc">Exclusive end: the end of the current hour or UTC day.</param>
    /// <param name="Period"><c>24h</c>, <c>7d</c>, <c>30d</c> or <c>90d</c>.</param>
    /// <param name="Bucket"><c>hour</c> for <c>24h</c>, <c>day</c> otherwise.</param>
    /// <param name="Entities">Travelers, flights, airlines, airports, lounges, loyalty programs, in
    /// that order.</param>
    public sealed record AdminStatsDto(
        DateTimeOffset FromUtc,
        DateTimeOffset ToUtc,
        string Period,
        string Bucket,
        IReadOnlyList<AdminEntityStatsDto> Entities
    );

    /// <summary>One dashboard card.</summary>
    /// <param name="Key">The card's data browser table slug, e.g. <c>loyalty-programs</c>.</param>
    /// <param name="DateColumn">The column "added" comes from: <c>CreatedUtc</c>, or <c>FirstSeenAt</c>
    /// for airlines and airports.</param>
    /// <param name="NewInPeriod">Rows added in the window. Rows with no known date never count, and
    /// neither do seeded airlines and airports. The data browser filter can't exclude seeded rows, so
    /// on an environment seeded inside the window its airline and airport lists can include rows this
    /// number doesn't.</param>
    /// <param name="Verified">Null for travelers and flights, which have no verified state.</param>
    /// <param name="Unverified"><c>Total - Verified</c>; null where <c>Verified</c> is.</param>
    /// <param name="Series">New rows per bucket, oldest first.</param>
    public sealed record AdminEntityStatsDto(
        string Key,
        string DateColumn,
        int Total,
        int NewInPeriod,
        int? Verified,
        int? Unverified,
        IReadOnlyList<int> Series
    );
}
