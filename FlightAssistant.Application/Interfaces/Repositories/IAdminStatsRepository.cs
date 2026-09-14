namespace FlightAssistant.Application.Interfaces.Repositories
{
    /// <summary>
    /// One dashboard card's raw counts (#336).
    /// </summary>
    /// <param name="Key">The card, named by its browsable table slug so the panel can link to
    /// <c>/data/{Key}</c>.</param>
    /// <param name="DateColumn">The column "added" is read from, so the panel can build the matching
    /// <c>{DateColumn}:&gt;=fromUtc</c> data browser filter.</param>
    /// <param name="Verified">Null for an entity with no verified state (travelers, flights).</param>
    /// <param name="AddedUtc">When each row counted as new in the window was added, in no particular
    /// order. Rows with an unknown date, and seeded airlines and airports, are never included.</param>
    public sealed record AdminEntityCounts(
        string Key,
        string DateColumn,
        int Total,
        int? Verified,
        IReadOnlyList<DateTime> AddedUtc);

    public interface IAdminStatsRepository
    {
        /// <summary>
        /// Counts for every dashboard card, in display order: travelers, flights, airlines, airports,
        /// lounges, loyalty programs. A row is added in the window when its date column is on or
        /// after <paramref name="fromUtc"/> and before <paramref name="toUtc"/>. Both bounds must
        /// carry <see cref="DateTimeKind.Utc"/>, since Npgsql refuses anything else against a
        /// <c>timestamptz</c> column.
        /// </summary>
        Task<IReadOnlyList<AdminEntityCounts>> GetCountsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    }
}
