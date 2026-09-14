using FlightAssistant.Application.Interfaces.Repositories;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Stats
{
    /// <summary>One dashboard card's numbers for the window.</summary>
    /// <param name="NewInPeriod">Always the sum of <paramref name="Series"/>.</param>
    /// <param name="Unverified"><c>Total - Verified</c>; null where <paramref name="Verified"/> is.</param>
    /// <param name="Series">New rows per bucket, oldest first: bucket <c>i</c> starts at
    /// <c>FromUtc + i × bucket</c>.</param>
    public sealed record AdminEntityStatsResult(
        string Key,
        string DateColumn,
        int Total,
        int NewInPeriod,
        int? Verified,
        int? Unverified,
        IReadOnlyList<int> Series);

    /// <param name="FromUtc">The window's inclusive start, on a bucket boundary.</param>
    /// <param name="ToUtc">The window's exclusive end: the end of the current hour or UTC day, so it
    /// can be slightly in the future.</param>
    /// <param name="Bucket"><c>hour</c> or <c>day</c>.</param>
    public sealed record AdminStatsResult(
        DateTimeOffset FromUtc,
        DateTimeOffset ToUtc,
        string Period,
        string Bucket,
        IReadOnlyList<AdminEntityStatsResult> Entities);

    /// <summary>
    /// The dashboard cards' totals, new rows in a period, and verified state (#336). Backs
    /// <c>GET /admin/stats</c>.
    /// </summary>
    /// <param name="Period"><c>24h</c>, <c>7d</c>, <c>30d</c> or <c>90d</c>; null means <c>7d</c>.</param>
    public sealed record GetAdminStatsQuery(string? Period = null) : IRequest<AdminStatsResult>;

    /// <summary>
    /// A period as a window of whole buckets ending with the current one. Aligned rather than rolling,
    /// so "new in period" equals the sum of its series, and the panel can link the number to the data
    /// browser with the very same <c>fromUtc</c>.
    /// </summary>
    internal static class StatsWindow
    {
        public const string DefaultPeriod = "7d";

        public static readonly IReadOnlyDictionary<string, (string Bucket, int Count)> Periods =
            new Dictionary<string, (string, int)>(StringComparer.Ordinal)
            {
                ["24h"] = ("hour", 24),
                ["7d"] = ("day", 7),
                ["30d"] = ("day", 30),
                ["90d"] = ("day", 90),
            };

        public static (string Period, string Bucket, TimeSpan BucketSize, int BucketCount, DateTimeOffset FromUtc, DateTimeOffset ToUtc)
            Resolve(string? period, DateTimeOffset now)
        {
            var key = period ?? DefaultPeriod;
            var (bucket, count) = Periods[key];
            var size = bucket == "hour" ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
            var utc = now.ToUniversalTime();
            var currentBucketStart = bucket == "hour"
                ? new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero)
                : new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
            var toUtc = currentBucketStart + size;
            return (key, bucket, size, count, toUtc - size * count, toUtc);
        }
    }

    public sealed class GetAdminStatsQueryValidator : AbstractValidator<GetAdminStatsQuery>
    {
        public GetAdminStatsQueryValidator()
        {
            // Matched exactly: "7D" is a 400, not a lenient match, like every other admin query value.
            RuleFor(x => x.Period)
                .Must(period => period is null || StatsWindow.Periods.ContainsKey(period))
                .OverridePropertyName("period")
                .WithMessage(x => $"Unknown period '{x.Period}'. Use one of: {string.Join(", ", StatsWindow.Periods.Keys)}.");
        }
    }

    public sealed class GetAdminStatsQueryHandler : IRequestHandler<GetAdminStatsQuery, AdminStatsResult>
    {
        private readonly IAdminStatsRepository _stats;
        private readonly TimeProvider _timeProvider;

        public GetAdminStatsQueryHandler(IAdminStatsRepository stats, TimeProvider timeProvider)
        {
            _stats = stats;
            _timeProvider = timeProvider;
        }

        public async Task<AdminStatsResult> Handle(GetAdminStatsQuery request, CancellationToken ct)
        {
            var window = StatsWindow.Resolve(request.Period, _timeProvider.GetUtcNow());
            var fromUtc = window.FromUtc.UtcDateTime;
            var counts = await _stats.GetCountsAsync(fromUtc, window.ToUtc.UtcDateTime, ct);

            var entities = counts.Select(card =>
            {
                var series = new int[window.BucketCount];
                foreach (var added in card.AddedUtc)
                {
                    // The repository already bounds these; a time outside the window is dropped rather
                    // than clamped into an edge bucket, so the series never over-reports.
                    var offset = added - fromUtc;
                    if (offset < TimeSpan.Zero) continue;
                    var index = offset.Ticks / window.BucketSize.Ticks;
                    if (index < series.Length) series[index]++;
                }

                return new AdminEntityStatsResult(
                    card.Key,
                    card.DateColumn,
                    card.Total,
                    series.Sum(),
                    card.Verified,
                    card.Verified is null ? null : card.Total - card.Verified,
                    series);
            }).ToList();

            return new AdminStatsResult(window.FromUtc, window.ToUtc, window.Period, window.Bucket, entities);
        }
    }
}
