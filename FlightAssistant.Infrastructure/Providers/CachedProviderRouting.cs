using System.Collections.Concurrent;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Providers;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.Providers
{
    /// <summary>Reads one routing key's persisted row; null means no row — nobody has changed it.</summary>
    internal delegate Task<ProviderRouteState?> ProviderRouteReader(string job, CancellationToken ct);

    /// <summary>Supplies the configured default for a key, used when no row exists or a read fails.</summary>
    internal delegate IReadOnlyList<string> ConfiguredDefaultReader(string job);

    /// <summary>
    /// <see cref="IProviderRouting"/> backed by <c>provider_routes</c>.
    /// <see cref="GetProvidersAsync"/> is cached per key for <see cref="CacheWindow"/>;
    /// <see cref="GetAsync"/> is not.
    ///
    /// <para>
    /// <b>No fail-open/fail-closed split, unlike <c>CachedJobSwitch</c>.</b> That class needs one
    /// because a boolean read failure has two plausible wrong answers, and guessing "enabled" can
    /// spend real money. A routing read failure has one unambiguous right answer — the configured
    /// default — which is always available in-process. Logged at Warning and cached like a real
    /// answer, so the retry happens when the entry ages out rather than on a separate schedule.
    /// </para>
    /// </summary>
    public sealed class CachedProviderRouting : IProviderRouting
    {
        /// <summary>
        /// How long a read is trusted. This is also the slowest a change can take to apply, which is
        /// the number an operator notices after pressing the button, so it is kept short rather than
        /// tuned for read volume. Same value and same reasoning as <c>CachedJobSwitch.CacheWindow</c>.
        /// </summary>
        public static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(10);

        private readonly ProviderRouteReader _read;
        private readonly ConfiguredDefaultReader _configuredDefault;
        private readonly TimeProvider _time;
        private readonly ILogger<CachedProviderRouting> _logger;

        // Per-key entry. Every read either hits an existing entry or replaces it wholesale, never
        // mutates one in place, so no external lock is needed and a duplicate read on a race is
        // harmless.
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

        public CachedProviderRouting(
            IServiceScopeFactory scopeFactory,
            IProviderRouteDefaults defaults,
            TimeProvider time,
            ILogger<CachedProviderRouting> logger)
            : this(ReadFromDatabase(scopeFactory), defaults.For, time, logger)
        {
        }

        /// <summary>Test seam: substitutes both reads, so the caching policy can be proven without a
        /// database or a configuration root.</summary>
        internal CachedProviderRouting(
            ProviderRouteReader read,
            ConfiguredDefaultReader configuredDefault,
            TimeProvider time,
            ILogger<CachedProviderRouting> logger)
        {
            _read = read;
            _configuredDefault = configuredDefault;
            _time = time;
            _logger = logger;
        }

        /// <summary>One scope, one row, per call. The singleton never holds a scoped
        /// <see cref="FlightAssistantDbContext"/> itself — the same shape <c>CachedJobSwitch</c>
        /// uses for the same captive-dependency reason.</summary>
        private static ProviderRouteReader ReadFromDatabase(IServiceScopeFactory scopeFactory) => async (job, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            var row = await db.ProviderRoutes.AsNoTracking().FirstOrDefaultAsync(r => r.Job == job, ct);
            return row is null
                ? null
                : new ProviderRouteState(row.Job, Split(row.Providers), false, row.ChangedUtc, row.ChangedBy, row.Reason);
        };

        /// <inheritdoc/>
        public async Task<IReadOnlyList<string>> GetProvidersAsync(string job, CancellationToken ct)
        {
            var now = _time.GetUtcNow();
            if (_cache.TryGetValue(job, out var cached) && now - cached.ReadUtc < CacheWindow)
                return cached.Providers;

            IReadOnlyList<string> providers;
            try
            {
                var state = await _read(job, ct);
                providers = state?.Providers ?? _configuredDefault(job);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Failed to read provider route {Job}; using the configured default.", job);
                providers = _configuredDefault(job);
            }

            _cache[job] = new CacheEntry(providers, now);
            return providers;
        }

        /// <inheritdoc/>
        public async Task<ProviderRouteState> GetAsync(string job, CancellationToken ct)
        {
            // Deliberately NOT wrapped in the fallback above: this backs the admin page reading its
            // own state back after a write, so a read failure should surface as an error rather than
            // silently render the configured default as though it were the stored row.
            var state = await _read(job, ct);
            return state ?? new ProviderRouteState(job, _configuredDefault(job), true, null, null, null);
        }

        internal static IReadOnlyList<string> Split(string providers) =>
            providers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private readonly record struct CacheEntry(IReadOnlyList<string> Providers, DateTimeOffset ReadUtc);
    }
}
