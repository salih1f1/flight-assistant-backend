using System.Globalization;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Admin.Usage;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.ProviderBudget
{
    /// <summary>
    /// Compares this month's AeroAPI spend (<c>TotalCostUsd</c>, the figure the admin gauge uses) with
    /// the budget (#303).
    ///
    /// <para>
    /// <b>The same report as the gauge.</b> It reads the current UTC month to date with the usage
    /// report's own range and cache entry, so the guard and the gauge share one upstream call every 10
    /// minutes and can't disagree. <c>/account/usage</c> is billed at $0.00.
    /// </para>
    ///
    /// <para>
    /// <b>Fails open.</b> A report it can't read gives <see cref="ProviderBudgetState.Normal"/>: a failed
    /// read almost always means AeroAPI itself is failing, and then the calls it would hold back fail
    /// too. The failure is remembered for 10 minutes, so every search and sync doesn't spend a throttle
    /// slot retrying it.
    /// </para>
    ///
    /// <para>
    /// <b>A month's first 10 minutes are Normal without a read.</b> The usage range can't be cached
    /// before then, so every check would call upstream, and AeroAPI hasn't refreshed the new month's
    /// figures yet.
    /// </para>
    /// </summary>
    public sealed class ProviderBudgetGuard : IProviderBudgetGuard
    {
        private const string ReadFailedKey = "aeroapi:budget-guard:read-failed";

        private readonly IAeroApiAccountUsageProvider _usage;
        private readonly ICacheService _cache;
        private readonly IAeroApiBudgetOptions _budget;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<ProviderBudgetGuard> _logger;

        public ProviderBudgetGuard(
            IAeroApiAccountUsageProvider usage,
            ICacheService cache,
            IAeroApiBudgetOptions budget,
            TimeProvider timeProvider,
            ILogger<ProviderBudgetGuard> logger)
        {
            _usage = usage;
            _cache = cache;
            _budget = budget;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task<ProviderBudgetState> GetStateAsync(CancellationToken ct = default)
        {
            var now = _timeProvider.GetUtcNow();
            var (startUtc, endUtc) = UsageRange.Resolve(new GetAeroApiUsageQuery(), now, AeroApiUsageCache.Duration);
            if (endUtc - startUtc < AeroApiUsageCache.Duration)
                return ProviderBudgetState.Normal;

            try
            {
                if (await _cache.GetAsync<string>(ReadFailedKey, ct) is not null)
                    return ProviderBudgetState.Normal;

                var (usage, fetched) = await AeroApiUsageCache.GetOrFetchAsync(_usage, _cache, startUtc, endUtc, ct);
                var state = Classify(usage.TotalCostUsd);

                // Once per fresh report, not on every check.
                if (fetched && state != ProviderBudgetState.Normal)
                    _logger.LogWarning(
                        "AeroAPI spend this month is ${SpentUsd} of the ${BudgetUsd} budget ({State}): holding back calls",
                        usage.TotalCostUsd, _budget.MonthlyBudgetUsd, state);

                return state;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Couldn't read this month's AeroAPI spend; not holding back calls for {Backoff}",
                    AeroApiUsageCache.Duration);
                await MarkReadFailedAsync(now, ct);
                return ProviderBudgetState.Normal;
            }
        }

        private ProviderBudgetState Classify(decimal spentUsd)
        {
            if (spentUsd >= _budget.MonthlyBudgetUsd)
                return ProviderBudgetState.Exhausted;

            return spentUsd >= _budget.MonthlyBudgetUsd * _budget.SoftLimitRatio
                ? ProviderBudgetState.SoftLimit
                : ProviderBudgetState.Normal;
        }

        /// <summary>A cache failure here is only logged: the caller still gets Normal.</summary>
        private async Task MarkReadFailedAsync(DateTimeOffset now, CancellationToken ct)
        {
            try
            {
                await _cache.SetAsync(ReadFailedKey, now.ToString("O", CultureInfo.InvariantCulture),
                    AeroApiUsageCache.Duration, ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Couldn't remember the failed AeroAPI spend read");
            }
        }
    }
}
