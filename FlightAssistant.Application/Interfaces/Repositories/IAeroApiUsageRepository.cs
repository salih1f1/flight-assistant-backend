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
    /// <param name="CallsByTemplate">Every call in the range, whatever its outcome.</param>
    /// <param name="BillableCallsByTemplate">
    /// The same grouping with <see cref="AeroApiCallOutcome.Throttled"/> rows excluded. A 429 is
    /// refused by FlightAware before it is served and is not a billed query, so counting it inflates
    /// spend exactly when the provider is struggling. <see cref="AeroApiCallOutcome.Failed"/> rows
    /// are deliberately still counted here — a 4xx is plausibly billed and a 5xx plausibly is not,
    /// and without the invoice the conservative direction is to keep them in.
    /// </param>
    /// <param name="TransportFailureCountLast7Days">
    /// Calls that never got a response at all (<see cref="AeroApiCallOutcome.Failed"/> with a null
    /// status): unreachable host, timeout, unparseable reply. This — not an upstream 502, which
    /// AeroAPI does not send — is what this app maps to a client-facing 502.
    /// </param>
    public sealed record AeroApiUsageRawData(
        IReadOnlyDictionary<string, int> CallsByTemplate,
        IReadOnlyDictionary<string, int> BillableCallsByTemplate,
        IReadOnlyDictionary<AeroApiCallOutcome, int> CallsByOutcome,
        IReadOnlyDictionary<DateOnly, int> CallsByDay,
        IReadOnlyDictionary<string, int> CallsByTemplateForCurrentMonth,
        int Status429CountLast7Days,
        int TransportFailureCountLast7Days);

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
