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
    /// Defaults to the current UTC month through now when either bound is omitted (see the handler).
    /// </summary>
    public record GetAeroApiUsageQuery(DateTime? FromUtc, DateTime? ToUtc) : IRequest<AeroApiUsageResult>;

    public class GetAeroApiUsageQueryValidator : AbstractValidator<GetAeroApiUsageQuery>
    {
        public const int MaxRangeDays = 366;

        public GetAeroApiUsageQueryValidator()
        {
            RuleFor(x => x)
                .Must(x => x.FromUtc is null || x.ToUtc is null || x.FromUtc.Value < x.ToUtc.Value)
                .WithMessage("FromUtc must be earlier than ToUtc.")
                .WithName(nameof(GetAeroApiUsageQuery.FromUtc));

            RuleFor(x => x)
                .Must(x => x.FromUtc is null || x.ToUtc is null || (x.ToUtc.Value - x.FromUtc.Value).TotalDays <= MaxRangeDays)
                .WithMessage($"The range between FromUtc and ToUtc must not exceed {MaxRangeDays} days.")
                .WithName(nameof(GetAeroApiUsageQuery.FromUtc));
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
            var fromUtc = request.FromUtc ?? new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var toUtc = request.ToUtc ?? now;

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
