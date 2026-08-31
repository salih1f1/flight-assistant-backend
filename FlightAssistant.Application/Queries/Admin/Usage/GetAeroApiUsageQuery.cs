using FlightAssistant.Application.Interfaces.Configuration;
using FlightAssistant.Application.Interfaces.Repositories;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Usage
{
    public sealed record AeroApiUsageTemplateBreakdown(string EndpointTemplate, int Calls, decimal SpendUsd, decimal Share);

    public sealed record AeroApiUsageOutcomeBreakdown(string Outcome, int Calls);

    public sealed record AeroApiUsageDailyCount(DateOnly Date, int Calls);

    public sealed record AeroApiUsageResult(
        DateTime FromUtc,
        DateTime ToUtc,
        int TotalCalls,
        decimal EstimatedSpendUsd,
        decimal MonthlyBudgetUsd,
        int CurrentMonthCalls,
        decimal CurrentMonthSpendUsd,
        IReadOnlyList<AeroApiUsageTemplateBreakdown> ByTemplate,
        IReadOnlyList<AeroApiUsageOutcomeBreakdown> ByOutcome,
        IReadOnlyList<AeroApiUsageDailyCount> DailyCounts,
        int Status429CountLast7Days,
        int Status502CountLast7Days);

    /// <summary>
    /// Defaults to the current UTC month through now when either bound is omitted. Both
    /// <see cref="GetAeroApiUsageQueryValidator"/> (so the 366-day cap applies to the range that will
    /// actually be queried, not just the supplied bound) and the handler compute the effective range
    /// through <see cref="EffectiveFromUtc"/>/<see cref="EffectiveToUtc"/> — one source of truth for
    /// the defaulting/normalization logic, rather than two copies that could drift.
    /// </summary>
    public record GetAeroApiUsageQuery(DateTime? FromUtc, DateTime? ToUtc) : IRequest<AeroApiUsageResult>
    {
        /// <summary>
        /// A null bound defaults to the first instant of the current UTC month; a supplied bound is
        /// normalized to UTC — see <see cref="NormalizeToUtc"/>.
        /// </summary>
        public DateTime EffectiveFromUtc(DateTime nowUtc) =>
            NormalizeToUtc(FromUtc) ?? new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>A null bound defaults to <paramref name="nowUtc"/>; a supplied bound is normalized to UTC.</summary>
        public DateTime EffectiveToUtc(DateTime nowUtc) => NormalizeToUtc(ToUtc) ?? nowUtc;

        /// <summary>
        /// ASP.NET Core's query-string binding hands back <c>Kind=Unspecified</c> for a bare date or
        /// an offset-less timestamp (e.g. <c>?fromUtc=2026-01-01</c> — what an HTML date picker
        /// sends), and only <c>Kind=Utc</c> for one with a <c>Z</c>/offset suffix. <c>CalledUtc</c>
        /// maps to Postgres <c>timestamptz</c>, and Npgsql refuses to write any
        /// <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> is not <see cref="DateTimeKind.Utc"/>
        /// — anything else throws at parameter-write time, which is a 500 the caller cannot fix by
        /// resubmitting the same value differently. So every bound is normalized before it reaches
        /// the repository: <c>Unspecified</c> is read as UTC (the right interpretation for a
        /// parameter literally named <c>FromUtc</c>/<c>ToUtc</c>), and <c>Local</c> is converted.
        /// </summary>
        private static DateTime? NormalizeToUtc(DateTime? value)
        {
            if (value is null) return null;

            return value.Value.Kind switch
            {
                DateTimeKind.Utc => value.Value,
                DateTimeKind.Local => value.Value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), // Unspecified
            };
        }
    }

    public class GetAeroApiUsageQueryValidator : AbstractValidator<GetAeroApiUsageQuery>
    {
        public const int MaxRangeDays = 366;

        public GetAeroApiUsageQueryValidator()
        {
            RuleFor(x => x)
                .Must(HaveFromBeforeTo)
                .WithMessage("FromUtc must be earlier than ToUtc.")
                .WithName(nameof(GetAeroApiUsageQuery.FromUtc));

            RuleFor(x => x)
                .Must(NotExceedMaxRangeDays)
                .WithMessage($"The range between FromUtc and ToUtc must not exceed {MaxRangeDays} days.")
                .WithName(nameof(GetAeroApiUsageQuery.FromUtc));
        }

        // Evaluated over the EFFECTIVE range (defaults applied), not just the supplied bounds — a
        // request that omits one bound (e.g. only FromUtc=2000-01-01) still gets the default applied
        // to the other before the cap is checked, so the cap cannot be bypassed by omitting a bound.
        private static bool HaveFromBeforeTo(GetAeroApiUsageQuery query)
        {
            var now = DateTime.UtcNow;
            return query.EffectiveFromUtc(now) < query.EffectiveToUtc(now);
        }

        private static bool NotExceedMaxRangeDays(GetAeroApiUsageQuery query)
        {
            var now = DateTime.UtcNow;
            return (query.EffectiveToUtc(now) - query.EffectiveFromUtc(now)).TotalDays <= MaxRangeDays;
        }
    }

    public sealed class GetAeroApiUsageQueryHandler : IRequestHandler<GetAeroApiUsageQuery, AeroApiUsageResult>
    {
        private readonly IAeroApiUsageRepository _repo;
        private readonly IAeroApiUsagePricingOptions _pricing;

        public GetAeroApiUsageQueryHandler(IAeroApiUsageRepository repo, IAeroApiUsagePricingOptions pricing)
        {
            _repo = repo;
            _pricing = pricing;
        }

        public async Task<AeroApiUsageResult> Handle(GetAeroApiUsageQuery request, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var fromUtc = request.EffectiveFromUtc(now);
            var toUtc = request.EffectiveToUtc(now);

            var currentMonthStartUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var last7DaysStartUtc = now.AddDays(-7);

            var raw = await _repo.GetUsageRollupAsync(
                fromUtc, toUtc,
                currentMonthStartUtc, now,
                last7DaysStartUtc, now,
                ct);

            return BuildResult(fromUtc, toUtc, raw, _pricing);
        }

        /// <summary>
        /// All pricing/budget arithmetic — the part that can be wrong in a way that costs real
        /// money — lives here as a pure function over already-grouped counts, so it is testable
        /// without a database. Public so it can be exercised directly, in addition to through
        /// <see cref="Handle"/>.
        /// </summary>
        public static AeroApiUsageResult BuildResult(
            DateTime fromUtc, DateTime toUtc, AeroApiUsageRawData raw, IAeroApiUsagePricingOptions pricing)
        {
            var totalCalls = raw.CallsByTemplate.Values.Sum();

            var byTemplate = raw.CallsByTemplate
                .Select(kv => new AeroApiUsageTemplateBreakdown(
                    kv.Key,
                    kv.Value,
                    PriceFor(pricing, kv.Key) * kv.Value,
                    totalCalls == 0 ? 0m : Math.Round((decimal)kv.Value / totalCalls, 4)))
                .OrderByDescending(t => t.Calls)
                .ThenBy(t => t.EndpointTemplate, StringComparer.Ordinal)
                .ToList();

            var estimatedSpendUsd = byTemplate.Sum(t => t.SpendUsd);

            var byOutcome = raw.CallsByOutcome
                .Select(kv => new AeroApiUsageOutcomeBreakdown(kv.Key.ToString(), kv.Value))
                .OrderByDescending(o => o.Calls)
                .ThenBy(o => o.Outcome, StringComparer.Ordinal)
                .ToList();

            var dailyCounts = raw.CallsByDay
                .OrderBy(kv => kv.Key)
                .Select(kv => new AeroApiUsageDailyCount(kv.Key, kv.Value))
                .ToList();

            var currentMonthCalls = raw.CallsByTemplateForCurrentMonth.Values.Sum();
            var currentMonthSpendUsd = raw.CallsByTemplateForCurrentMonth
                .Sum(kv => PriceFor(pricing, kv.Key) * kv.Value);

            return new AeroApiUsageResult(
                fromUtc,
                toUtc,
                totalCalls,
                estimatedSpendUsd,
                pricing.MonthlyBudgetUsd,
                currentMonthCalls,
                currentMonthSpendUsd,
                byTemplate,
                byOutcome,
                dailyCounts,
                raw.Status429CountLast7Days,
                raw.Status502CountLast7Days);
        }

        // Unknown template prices at 0 rather than throwing — see IAeroApiUsagePricingOptions.Pricing.
        private static decimal PriceFor(IAeroApiUsagePricingOptions pricing, string endpointTemplate) =>
            pricing.Pricing.TryGetValue(endpointTemplate, out var price) ? price : 0m;
    }
}
