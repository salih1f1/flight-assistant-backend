using FlightAssistant.Domain.Configuration.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    /// <summary>
    /// Raw grouped counts behind the AeroAPI usage rollup (<c>GET /admin/usage/aeroapi</c>). Every
    /// grouping happens in SQL — see <c>AeroApiUsageRepository</c> in Infrastructure — so
    /// <c>GetAeroApiUsageQueryHandler</c> never pulls <c>aeroapi_calls</c> rows into memory to group
    /// them; the table grows unbounded until the retention prune runs. The handler owns the pricing
    /// and budget arithmetic over these counts, not this repository.
    /// </summary>
    public sealed record AeroApiUsageRawData(
        IReadOnlyDictionary<string, int> CallsByTemplate,
        IReadOnlyDictionary<AeroApiCallOutcome, int> CallsByOutcome,
        IReadOnlyDictionary<DateOnly, int> CallsByDay,
        IReadOnlyDictionary<string, int> CallsByTemplateForCurrentMonth,
        int Status429CountLast7Days,
        int Status502CountLast7Days);

    public interface IAeroApiUsageRepository
    {
        /// <summary>
        /// Every window is a half-open UTC interval — [start, end) — supplied by the caller: the
        /// query handler decides what "current month" and "trailing 7 days" mean, this repository
        /// only executes the grouped counts for whatever windows it is given.
        /// </summary>
        Task<AeroApiUsageRawData> GetUsageRollupAsync(
            DateTime rangeFromUtc,
            DateTime rangeToUtc,
            DateTime currentMonthStartUtc,
            DateTime currentMonthEndUtc,
            DateTime last7DaysStartUtc,
            DateTime last7DaysEndUtc,
            CancellationToken ct = default);
    }
}
