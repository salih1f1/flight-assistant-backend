# AeroAPI Budget Guard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop this month's AeroAPI spend from silently overrunning `AeroApi:MonthlyBudgetUsd` (#303). Past a soft limit, uncached searches are refused and Live polls slow down. At the budget, only flights near departure make calls.

**Architecture:**
- **The guard (Application).** `ProviderBudgetGuard` reads this month's `TotalCostUsd` from AeroAPI's own usage report. It uses the same range and cache entry as `GET /admin/usage/aeroapi`, through a new shared `AeroApiUsageCache` helper, and returns `Normal`, `SoftLimit` or `Exhausted`.
- **Search (Infrastructure).** `AeroApiFlightSearchBudgetDecorator` sits between `AeroApiFlightSearchCachingDecorator` and `AeroApiFlightSearchProvider`. A cached search is still answered; a cache miss past the soft limit gets a 503 that names the budget.
- **Status sync (Application).** `SyncFlightStatusCommandHandler` asks the guard before any call for a flight that isn't in Fast mode:
  - past the soft limit, it plans the next sync at twice the Live interval;
  - once the budget is exhausted, the loop makes no call for that flight, and a person's sync gets a 503.

**Tech Stack:** .NET 10, MediatR, FluentValidation, xUnit, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`.

**Spec:** `docs/superpowers/plans/2026-09-15-flight-data-cost-reduction.md` (Phase 1, item 6) and issue #303.

## Global Constraints

- Work only in the worktree `/Users/salihalishov/Repositories/flight-assistant-backend-303`, branch `feature/303-aeroapi-budget-guard`. Never touch the main checkout or other worktrees.
- **The spend figure.**
  - The guard reads `AeroApiAccountUsage.TotalCostUsd` for the current UTC month to date.
  - It reads through `AeroApiUsageCache.GetOrFetchAsync`, with the range from `UsageRange.Resolve(new GetAeroApiUsageQuery(), now, AeroApiUsageCache.Duration)`. That is the same range and cache entry the admin usage report uses.
  - No local call counting or pricing (CLAUDE.md, PR #294).
- **The states.** `public enum ProviderBudgetState { Normal, SoftLimit, Exhausted }` in `FlightAssistant.Application.ProviderBudget`:
  - `SoftLimit` when spend ≥ `SoftLimitRatio` × `MonthlyBudgetUsd`;
  - `Exhausted` when spend ≥ `MonthlyBudgetUsd`.
- **The guard API.** `public interface IProviderBudgetGuard { Task<ProviderBudgetState> GetStateAsync(CancellationToken ct = default); }` in `FlightAssistant.Application.ProviderBudget`.
- **The new setting.** `AeroApi:SoftLimitRatio`:
  - a `decimal`, default `0.8m`, validated with `[Range(0.1, 1.0)]`;
  - on `AeroApiOptions` and `IAeroApiBudgetOptions`.
  - `MonthlyBudgetUsd` keeps its default and range.
- **The guard fails open.**
  - A failed read returns `Normal`, and the failure is remembered for 10 minutes under `aeroapi:budget-guard:read-failed`.
  - A month's first 10 minutes return `Normal` without a read.
  - Cancellation propagates.
- **Behaviour.**
  - **Search:** from `SoftLimit`, a cache miss gets a 503 naming the budget, and a cached search is still answered.
  - **Sync:** a Fast-mode flight never reads the budget. From `SoftLimit`, a Live flight's next sync is planned at 2 × `FlightSync:Interval`. At `Exhausted`, the loop makes no call for a flight that isn't in Fast mode, and a person's sync of such a flight gets a 503.
- **API and schema.**
  - No DTO, route, schema or migration change.
  - Endpoint descriptions change, and both sync endpoints gain `ProducesProblem(503)`. So `spec/openapi.json` is regenerated with `FlightSync__Enabled=false scripts/generate-openapi.sh` and committed in the task that changes it.
- **No paid calls.** Make no call to AeroAPI or any other paid external API. Every test uses stub transports or substitutes. Never run the API with sync on. Never run `dotnet ef database update`.
- **Test values.** Tests with option values use **non-default** values, so a hardcoded constant can't pass (#308).
- **The build stays warning-free** (#301).
- **TDD.** Every new test is seen failing first. Each task ends with a prove step: break the code on purpose, watch the named test fail, then restore. After restoring from a `.bak`, `touch` the file so MSBuild recompiles.
- **Commits.** Commit with explicit paths only (never `-A` or `.`). Every commit message ends with a blank line and `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`. Never push.

## Decisions made while planning

These are recorded so review doesn't relitigate them.

- **Search at the soft limit.** The plan sends a cache miss "to cache or the fallback provider only". There is no fallback until Phase 3, so for now a miss gets the 503.
  - This applies to every caller: anonymous, traveler and admin.
  - Timezone lookups happen inside the search provider, so a refused search makes none.
- **A person's sync at `Exhausted` gets a 503** that names the budget, not a silent `{ updated: false }` like the cooldown.
  - The cooldown's stored status is at most minutes old; here it can be an hour old.
  - #303 asks for a 503 that names the cause.
- **The loop's hold-back is silent.** A Live flight held back is re-planned, not failed, so the heartbeat's `FlightsFailed` doesn't count it. The guard's warning, logged once per fresh usage report, is the signal.
- **A Fast-mode flight never reads the budget.** The flights that matter most pay no extra throttle wait.
  - The cost: when a Fast flight's re-plan after the call turns it Live, that one next sync uses the normal Live interval.
- **The guard fails open.** A failed usage read almost always means AeroAPI itself is failing, and then the calls the guard would hold back fail too. Remembering the failure for 10 minutes stops every search and sync from spending a throttle slot on it.
- **`Retry-After` stays at 60 s.** That is `GlobalExceptionHandler`'s value for every 503. A refused search or sync costs no upstream call, so an early retry is harmless.
- **The guard reads `TotalCostUsd`,** the figure the admin panel's gauge compares with `monthlyBudgetUsd`.

## Commands

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-303
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ProviderBudgetGuardTests"
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~GetAeroApiUsageQueryTests"
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests"
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiOptionsTests|FullyQualifiedName~AeroApiDependencyInjectionTests"
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiFlightSearchBudgetDecoratorTests"
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightStatusSyncBackgroundServiceTests"
dotnet build --configuration Release
FlightSync__Enabled=false scripts/generate-openapi.sh
```

---

### Task 1: The budget guard reads this month's spend

**Files:**
- Create: `FlightAssistant.Application/ProviderBudget/IProviderBudgetGuard.cs`
- Create: `FlightAssistant.Application/ProviderBudget/ProviderBudgetGuard.cs`
- Modify: `FlightAssistant.Application/Queries/Admin/Usage/GetAeroApiUsageQuery.cs` (extract `AeroApiUsageCache`)
- Modify: `FlightAssistant.Application/Interfaces/ExternalServices/IAeroApiAccountUsageProvider.cs` (`IAeroApiBudgetOptions.SoftLimitRatio`)
- Modify: `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiOptions.cs`
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs`
- Modify: `CLAUDE.md`
- Test (create): `FlightAssistant.Application.Tests/ProviderBudget/ProviderBudgetGuardTests.cs`
- Test: `FlightAssistant.Application.Tests/Queries/Admin/Usage/GetAeroApiUsageQueryTests.cs` (`FakeBudget` only)
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiOptionsTests.cs`
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiDependencyInjectionTests.cs`

**Interfaces:**
- Produces:
  - `FlightAssistant.Application.ProviderBudget.ProviderBudgetState { Normal, SoftLimit, Exhausted }`
  - `FlightAssistant.Application.ProviderBudget.IProviderBudgetGuard` with `Task<ProviderBudgetState> GetStateAsync(CancellationToken ct = default)`
  - `FlightAssistant.Application.ProviderBudget.ProviderBudgetGuard(IAeroApiAccountUsageProvider usage, ICacheService cache, IAeroApiBudgetOptions budget, TimeProvider timeProvider, ILogger<ProviderBudgetGuard> logger)`, registered scoped as `IProviderBudgetGuard` in `AddInfrastructure`
  - `IAeroApiBudgetOptions.SoftLimitRatio` (`decimal`)
  - `internal static class AeroApiUsageCache` in `FlightAssistant.Application.Queries.Admin.Usage`, with `Duration` and `GetOrFetchAsync(provider, cache, startUtc, endUtc, ct)` returning `(AeroApiAccountUsage Usage, bool Fetched)`

Tasks 2 and 3 consume `IProviderBudgetGuard` and `ProviderBudgetState`.

- [ ] **Step 1: Write the failing guard tests.** Create `FlightAssistant.Application.Tests/ProviderBudget/ProviderBudgetGuardTests.cs`:

```csharp
using System.Globalization;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Queries.Admin.Usage;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlightAssistant.Application.Tests.ProviderBudget;

/// <summary>
/// The guard compares this month's AeroAPI spend with the budget (#303). What carries the risk: the
/// thresholds, reading the same cached report as the admin gauge (so a check costs no extra call), and
/// failing open without retrying a failed read on every check.
/// </summary>
public class ProviderBudgetGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 37, 22, TimeSpan.Zero);
    private static readonly DateTimeOffset MonthStart = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NowOnTheCacheGrid = new(2026, 9, 17, 10, 30, 0, TimeSpan.Zero);

    // Non-default values (#308): a $20 budget with the soft limit at 60%, so $12.
    private sealed class FakeBudget : IAeroApiBudgetOptions
    {
        public decimal MonthlyBudgetUsd => 20m;
        public decimal SoftLimitRatio => 0.6m;
    }

    /// <summary>A dictionary cache that ignores expiry, records every write, and can be made to fail.</summary>
    private sealed class FakeCache : ICacheService
    {
        private readonly Dictionary<string, object?> _entries = [];
        public List<(string Key, TimeSpan? Ttl)> Writes { get; } = [];
        public bool Fail { get; init; }

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("cache unavailable");
            return Task.FromResult(_entries.TryGetValue(key, out var value) ? (T?)value : default);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("cache unavailable");
            _entries[key] = value;
            Writes.Add((key, absoluteExpire));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private readonly IAeroApiAccountUsageProvider _provider = Substitute.For<IAeroApiAccountUsageProvider>();
    private readonly FakeCache _cache = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly CapturingLogger<ProviderBudgetGuard> _logger = new();

    private ProviderBudgetGuard Guard(ICacheService? cache = null) =>
        new(_provider, cache ?? _cache, new FakeBudget(), _clock, _logger);

    private void SpendIs(decimal costUsd) =>
        _provider.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new AeroApiAccountUsage(
                TotalCalls: 10,
                TotalPages: 12,
                TotalCostUsd: costUsd,
                TotalDiscountCostUsd: 0m,
                TotalSuccessfulCalls: 10,
                TotalFailedCalls: 0,
                ByOperation: []));

    private Task ReceivedUsageCalls(int count) =>
        _provider.Received(count).GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

    [Theory]
    [InlineData("0", ProviderBudgetState.Normal)]
    [InlineData("11.99", ProviderBudgetState.Normal)]
    [InlineData("12.00", ProviderBudgetState.SoftLimit)] // exactly 60% of $20
    [InlineData("19.99", ProviderBudgetState.SoftLimit)]
    [InlineData("20.00", ProviderBudgetState.Exhausted)] // exactly the budget
    [InlineData("31.40", ProviderBudgetState.Exhausted)]
    public async Task GetStateAsync_ComparesThisMonthsSpend_WithTheBudget(string spentUsd, ProviderBudgetState expected)
    {
        SpendIs(decimal.Parse(spentUsd, CultureInfo.InvariantCulture));

        Assert.Equal(expected, await Guard().GetStateAsync());
    }

    [Fact]
    public async Task GetStateAsync_ReadsTheMonthToDate_OnTheUsageReportsCacheGrid()
    {
        SpendIs(1m);

        await Guard().GetStateAsync();

        await _provider.Received(1).GetUsageAsync(MonthStart, NowOnTheCacheGrid, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStateAsync_SharesTheAdminUsageReportsCacheEntry()
    {
        SpendIs(1m);
        await Guard().GetStateAsync();

        // The admin gauge's default view, over the same cache: it must be a hit.
        var handler = new GetAeroApiUsageQueryHandler(
            _provider, _cache, new FakeBudget(), Substitute.For<IAeroApiThrottleObserver>(), _clock);
        var report = await handler.Handle(new GetAeroApiUsageQuery(), CancellationToken.None);

        Assert.Equal(1m, report.TotalCostUsd);
        await ReceivedUsageCalls(1);
    }

    [Fact]
    public async Task GetStateAsync_ACachedReport_IsNotFetchedAgain()
    {
        SpendIs(1m);
        var guard = Guard();

        await guard.GetStateAsync();
        await guard.GetStateAsync();

        await ReceivedUsageCalls(1);
    }

    [Theory]
    [InlineData("2026-10-01T00:00:00Z")]
    [InlineData("2026-10-01T00:09:59Z")]
    public async Task GetStateAsync_InAMonthsFirstTenMinutes_IsNormal_WithoutAReport(string now)
    {
        _clock.SetUtcNow(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture));
        SpendIs(50m);

        Assert.Equal(ProviderBudgetState.Normal, await Guard().GetStateAsync());
        await ReceivedUsageCalls(0);
    }

    [Fact]
    public async Task GetStateAsync_TenMinutesIntoAMonth_ReadsTheReport()
    {
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 1, 0, 10, 0, TimeSpan.Zero));
        SpendIs(50m);

        Assert.Equal(ProviderBudgetState.Exhausted, await Guard().GetStateAsync());
    }

    [Fact]
    public async Task GetStateAsync_AFailedRead_IsNormal_AndIsNotRetriedForTenMinutes()
    {
        _provider.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DomainException("Usage reporting is temporarily unavailable.", 502));
        var guard = Guard();

        Assert.Equal(ProviderBudgetState.Normal, await guard.GetStateAsync());
        Assert.Equal(ProviderBudgetState.Normal, await guard.GetStateAsync());

        await ReceivedUsageCalls(1);
        Assert.Contains(_cache.Writes, w => w.Key == "aeroapi:budget-guard:read-failed" && w.Ttl == TimeSpan.FromMinutes(10));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task GetStateAsync_ACacheOutage_IsNormal_WithoutAReport()
    {
        SpendIs(50m);

        Assert.Equal(ProviderBudgetState.Normal, await Guard(new FakeCache { Fail = true }).GetStateAsync());
        await ReceivedUsageCalls(0);
    }

    [Fact]
    public async Task GetStateAsync_Cancelled_Throws_AndRemembersNoFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _provider.GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Guard().GetStateAsync(cts.Token));
        Assert.Empty(_cache.Writes);
    }

    [Fact]
    public async Task GetStateAsync_PastTheSoftLimit_WarnsOncePerFreshReport()
    {
        SpendIs(12m);
        var guard = Guard();

        await guard.GetStateAsync();
        await guard.GetStateAsync();

        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task GetStateAsync_BelowTheSoftLimit_LogsNoWarning()
    {
        SpendIs(11.99m);

        await Guard().GetStateAsync();

        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Warning);
    }
}
```

- [ ] **Step 2: Write the failing options and DI tests.**

(a) In `GetAeroApiUsageQueryTests.cs`, give `FakeBudget` the new member:

```csharp
    private sealed class FakeBudget : IAeroApiBudgetOptions
    {
        public decimal MonthlyBudgetUsd => 5m;
        public decimal SoftLimitRatio => 0.8m;
    }
```

(b) In `AeroApiOptionsTests.cs`:
- In `ValidApiKey_BindsWithDocumentedDefaults`, directly below `Assert.Equal(5m, value.MonthlyBudgetUsd);`, add:

```csharp
        Assert.Equal(0.8m, value.SoftLimitRatio);
```

- Directly after the `MonthlyBudgetUsd_BindsAConfiguredValue` test, add:

```csharp
    [Theory]
    [InlineData("0")]     // a soft limit at $0 would hold back calls from the first cent
    [InlineData("0.05")]  // below the floor: almost certainly a typo
    [InlineData("1.01")]  // a soft limit past the hard stop would never apply
    public void SoftLimitRatioOutsideRange_FailsValidation(string ratio)
    {
        var options = BuildOptions(new Dictionary<string, string?>
        {
            ["AeroApi:ApiKey"] = "dummy",
            ["AeroApi:SoftLimitRatio"] = ratio
        });

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Fact]
    public void SoftLimitRatio_BindsAConfiguredValue()
    {
        var options = BuildOptions(new Dictionary<string, string?>
        {
            ["AeroApi:ApiKey"] = "dummy",
            ["AeroApi:SoftLimitRatio"] = "0.65"
        });

        Assert.Equal(0.65m, options.Value.SoftLimitRatio);
    }
```

(c) In `AeroApiDependencyInjectionTests.cs`:
- Add `using FlightAssistant.Application.ProviderBudget;`.
- In `AddInfrastructure_ResolvesIAeroApiBudgetOptions_FromAeroApiOptions`, below `Assert.Equal(5m, budget.MonthlyBudgetUsd);`, add:

```csharp
        Assert.Equal(0.8m, budget.SoftLimitRatio);
```

- Directly after that test, add:

```csharp
    [Fact]
    public void AddInfrastructure_ResolvesIProviderBudgetGuard_AsProviderBudgetGuard()
    {
        using var provider = BuildContainer();

        // The guard's own tests construct it by hand; this proves the host can build it (#303) from
        // the usage provider, the cache, the budget options and the TimeProvider AddInfrastructure registers.
        var guard = provider.GetRequiredService<IProviderBudgetGuard>();

        Assert.IsType<ProviderBudgetGuard>(guard);
    }
```

- [ ] **Step 3: Run the tests and watch them fail.**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ProviderBudgetGuardTests"`
Expected: build FAILS. `ProviderBudgetGuard`, `IProviderBudgetGuard`, `ProviderBudgetState` and `IAeroApiBudgetOptions.SoftLimitRatio` don't exist.

- [ ] **Step 4: Add the guard's interface.** Create `FlightAssistant.Application/ProviderBudget/IProviderBudgetGuard.cs`:

```csharp
namespace FlightAssistant.Application.ProviderBudget
{
    /// <summary>Where this month's AeroAPI spend stands against <c>AeroApi:MonthlyBudgetUsd</c> (#303).</summary>
    public enum ProviderBudgetState
    {
        /// <summary>Below the soft limit, or the spend couldn't be read.</summary>
        Normal,

        /// <summary>At or past <c>AeroApi:SoftLimitRatio</c> of the budget: only cached searches are
        /// answered, and Live polls run at twice their interval.</summary>
        SoftLimit,

        /// <summary>At or past the budget: only a flight in Fast mode makes a call.</summary>
        Exhausted
    }

    /// <summary>
    /// Says how hard to hold back AeroAPI calls this month (#303). The spend comes from AeroAPI's own
    /// usage report, cached 10 minutes and shared with the admin usage gauge, so a check makes no metered call.
    /// </summary>
    public interface IProviderBudgetGuard
    {
        Task<ProviderBudgetState> GetStateAsync(CancellationToken ct = default);
    }
}
```

- [ ] **Step 5: Extract the usage report's cache** in `GetAeroApiUsageQuery.cs`.

(a) Directly after the `UsageRange` class, add:

```csharp
    /// <summary>
    /// The one copy of how the usage report is cached. <see cref="GetAeroApiUsageQueryHandler"/> and
    /// <c>ProviderBudgetGuard</c> (#303) both read through it, so the admin gauge and the budget guard
    /// share one cache entry and one upstream call per range.
    /// </summary>
    internal static class AeroApiUsageCache
    {
        /// <summary>Matches FlightAware's own refresh cadence for this data.</summary>
        public static readonly TimeSpan Duration = TimeSpan.FromMinutes(10);

        private const string KeyPrefix = "aeroapi:account-usage:";
        private const string RangeFormat = "yyyy-MM-ddTHH:mm:ssZ";

        /// <summary>
        /// The cached report for the range, or a fresh one, which is then cached. A failed call throws
        /// and caches nothing. <c>Fetched</c> is true when this call went upstream.
        /// </summary>
        public static async Task<(AeroApiAccountUsage Usage, bool Fetched)> GetOrFetchAsync(
            IAeroApiAccountUsageProvider provider,
            ICacheService cache,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            CancellationToken ct)
        {
            var key = Key(startUtc, endUtc);

            var cached = await cache.GetAsync<AeroApiAccountUsage>(key, ct);
            if (cached is not null)
                return (cached, false);

            var usage = await provider.GetUsageAsync(startUtc, endUtc, ct);
            await cache.SetAsync(key, usage, Duration, ct);
            return (usage, true);
        }

        private static string Key(DateTimeOffset startUtc, DateTimeOffset endUtc) =>
            KeyPrefix
            + startUtc.UtcDateTime.ToString(RangeFormat, CultureInfo.InvariantCulture)
            + ".."
            + endUtc.UtcDateTime.ToString(RangeFormat, CultureInfo.InvariantCulture);
    }
```

(b) In `GetAeroApiUsageQueryHandler`:
- Replace

```csharp
        /// <summary>Matches FlightAware's own refresh cadence for this data.</summary>
        public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

        private const string CacheKeyPrefix = "aeroapi:account-usage:";
        private const string RangeFormat = "yyyy-MM-ddTHH:mm:ssZ";
```

with

```csharp
        /// <summary>Matches FlightAware's own refresh cadence for this data.</summary>
        public static readonly TimeSpan CacheDuration = AeroApiUsageCache.Duration;
```

- In `Handle`, replace

```csharp
            var key = CacheKey(startUtc, endUtc);

            // Only the upstream payload is cached — the budget and the throttle's next slot are
            // local state that must not be frozen for 10 minutes alongside it.
            var usage = await _cache.GetAsync<AeroApiAccountUsage>(key, ct);
            if (usage is null)
            {
                usage = await _usage.GetUsageAsync(startUtc, endUtc, ct);
                await _cache.SetAsync(key, usage, CacheDuration, ct);
            }
```

with

```csharp
            // Only the upstream payload is cached — the budget and the throttle's next slot are
            // local state that must not be frozen for 10 minutes alongside it.
            var (usage, _) = await AeroApiUsageCache.GetOrFetchAsync(_usage, _cache, startUtc, endUtc, ct);
```

- Delete the handler's private `CacheKey` method.

- [ ] **Step 6: Add the setting.**

(a) In `IAeroApiAccountUsageProvider.cs`, replace the `IAeroApiBudgetOptions` interface and its doc comment with:

```csharp
    /// <summary>
    /// The configured monthly spend budget, exposed here (in the Application layer) so the usage
    /// query can return it for the panel's gauge, and the budget guard can enforce it (#303), without
    /// depending on the Infrastructure-layer options class that owns it — Application never references
    /// Infrastructure. The concrete provider options type implements this interface, exactly as it does
    /// <see cref="IFlightSearchHorizonOptions"/>.
    /// </summary>
    public interface IAeroApiBudgetOptions
    {
        decimal MonthlyBudgetUsd { get; }

        /// <summary>The share of <see cref="MonthlyBudgetUsd"/> at which the budget guard's soft
        /// limit starts (#303).</summary>
        decimal SoftLimitRatio { get; }
    }
```

(b) In `AeroApiOptions.cs`, replace the `MonthlyBudgetUsd` property and its doc comment with:

```csharp
        /// <summary>
        /// The monthly spend budget — the personal tier this runs on is $5/month. The admin panel draws
        /// its usage gauge against it, and the budget guard enforces it (#303): past
        /// <see cref="SoftLimitRatio"/> of it, uncached searches are refused and Live polls slow down,
        /// and at the budget only Fast-mode flights make calls. Every cost figure still comes from
        /// AeroAPI's own <c>/account/usage</c> report, because FlightAware bills per page (not per call)
        /// and bills failed calls too, so nothing here prices anything locally.
        /// </summary>
        [Range(0.01, 10_000)]
        public decimal MonthlyBudgetUsd { get; set; } = 5m;

        /// <summary>
        /// The share of <see cref="MonthlyBudgetUsd"/> at which the budget guard's soft limit starts
        /// (#303). The floor stops a typo from holding back calls at almost no spend; 1.0 makes the
        /// soft limit the same as the hard stop.
        /// </summary>
        [Range(0.1, 1.0)]
        public decimal SoftLimitRatio { get; set; } = 0.8m;
```

- [ ] **Step 7: Write the guard.** Create `FlightAssistant.Application/ProviderBudget/ProviderBudgetGuard.cs`:

```csharp
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
```

- [ ] **Step 8: Register the guard.** In `FlightAssistant.Infrastructure/DependencyInjection.cs`:
- Add `using FlightAssistant.Application.ProviderBudget;`.
- Directly after the `services.AddScoped<IAeroApiAccountUsageProvider>(...)` registration, add:

```csharp
            // The budget guard (#303) reads this month's spend from the same cached usage report the
            // admin gauge uses. Flight search and status sync ask it before a call they can hold back.
            services.AddScoped<IProviderBudgetGuard, ProviderBudgetGuard>();
```

- [ ] **Step 9: Run the tests and watch them pass.**

Run:
- `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ProviderBudgetGuardTests|FullyQualifiedName~GetAeroApiUsageQueryTests"`
- `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiOptionsTests|FullyQualifiedName~AeroApiDependencyInjectionTests"`

Expected: all PASS. Every existing `GetAeroApiUsageQueryTests` test still passes unchanged; that proves the extraction kept the report's caching.

- [ ] **Step 10: Prove the tests.** Make each break, run the named tests, see them fail, then restore. Restore with `mv <file>.bak <file> && touch <file>`.
1. In `Classify`, change the soft-limit `>=` to `>`. `GetStateAsync_ComparesThisMonthsSpend_WithTheBudget(spentUsd: "12.00", …)` fails.
2. Delete the `if (endUtc - startUtc < AeroApiUsageCache.Duration) return …;` early return. `GetStateAsync_InAMonthsFirstTenMinutes_IsNormal_WithoutAReport` fails: a report was read.
3. Delete the `ReadFailedKey` read in `GetStateAsync`. `GetStateAsync_AFailedRead_IsNormal_AndIsNotRetriedForTenMinutes` fails with 2 calls.
4. Replace `AeroApiUsageCache.GetOrFetchAsync(...)` in the guard with `(await _usage.GetUsageAsync(startUtc, endUtc, ct), true)`. `GetStateAsync_SharesTheAdminUsageReportsCacheEntry` and `GetStateAsync_ACachedReport_IsNotFetchedAgain` fail with 2 calls.
5. Read `usage.TotalDiscountCostUsd` instead of `TotalCostUsd`. The threshold theory fails for every case above $0.
6. Change `SoftLimitRatio`'s default to `0.9m`. `ValidApiKey_BindsWithDocumentedDefaults` and `AddInfrastructure_ResolvesIAeroApiBudgetOptions_FromAeroApiOptions` fail.

- [ ] **Step 11: Document it in `CLAUDE.md`.**

(a) Directly after the paragraph that starts `**Usage and spend come from the provider, never from a local meter.**`, add this paragraph:

```markdown
**The budget guard enforces `AeroApi:MonthlyBudgetUsd` (#303).** `ProviderBudgetGuard` (Application) compares this month's `TotalCostUsd` — the figure the panel's gauge uses — with the budget: `Normal` below `AeroApi:SoftLimitRatio` of it, `SoftLimit` from there, `Exhausted` at the budget. It reads the usage report through `AeroApiUsageCache`, the same range and cache entry as `GET /admin/usage/aeroapi`, so the gauge and the guard share one free upstream call every 10 minutes and can't disagree. It fails open: a failed read is `Normal` and is remembered for 10 minutes, because a failed read almost always means AeroAPI itself is failing, when the calls the guard would hold back fail anyway, and retrying it on every check would spend a throttle slot each time. A month's first 10 minutes are `Normal` without a read, since the usage range can't be cached before then.
```

(b) In the Required Configuration table, replace the `AeroApi:MonthlyBudgetUsd` row with these two rows:

```markdown
| `AeroApi:MonthlyBudgetUsd` | Optional; default 5. The admin usage gauge's reference line and the budget the guard enforces (#303). Every cost figure comes from AeroAPI's own report, nothing is priced locally |
| `AeroApi:SoftLimitRatio` | Optional; default 0.8 (0.1–1.0). The share of the budget at which the guard starts holding back calls |
```

- [ ] **Step 12: Build.** Run `dotnet build --configuration Release`. Expected: 0 errors, 0 warnings.

- [ ] **Step 13: Commit.**

```bash
git add FlightAssistant.Application/ProviderBudget/IProviderBudgetGuard.cs \
  FlightAssistant.Application/ProviderBudget/ProviderBudgetGuard.cs \
  FlightAssistant.Application/Queries/Admin/Usage/GetAeroApiUsageQuery.cs \
  FlightAssistant.Application/Interfaces/ExternalServices/IAeroApiAccountUsageProvider.cs \
  FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiOptions.cs \
  FlightAssistant.Infrastructure/DependencyInjection.cs \
  CLAUDE.md \
  FlightAssistant.Application.Tests/ProviderBudget/ProviderBudgetGuardTests.cs \
  FlightAssistant.Application.Tests/Queries/Admin/Usage/GetAeroApiUsageQueryTests.cs \
  FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiOptionsTests.cs \
  FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiDependencyInjectionTests.cs
git commit -m "feat: a budget guard reads this month's AeroAPI spend from the cached usage report (#303)

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Past the soft limit, only cached searches are answered

**Files:**
- Create: `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightSearchBudgetDecorator.cs`
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs` (the `IFlightSearchProvider` registration)
- Modify: `FlightAssistant.API/Endpoints/FlightEndpoints.cs` (search description)
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs` (search description)
- Modify: `spec/openapi.json` (regenerated)
- Modify: `CLAUDE.md`
- Test (create): `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiFlightSearchBudgetDecoratorTests.cs`
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiDependencyInjectionTests.cs`

**Interfaces:**
- Consumes: `IProviderBudgetGuard.GetStateAsync(CancellationToken)` and `ProviderBudgetState` from Task 1.
- Produces: `public sealed class AeroApiFlightSearchBudgetDecorator : IFlightSearchProvider`, with constructor `(IFlightSearchProvider inner, IProviderBudgetGuard budget, ILogger<AeroApiFlightSearchBudgetDecorator> logger)`.

- [ ] **Step 1: Write the failing decorator tests.** Create `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiFlightSearchBudgetDecoratorTests.cs`:

```csharp
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>Past the budget guard's soft limit, a search that would reach AeroAPI is refused (#303).</summary>
public class AeroApiFlightSearchBudgetDecoratorTests
{
    /// <summary>Counts every search that reached the wrapped provider.</summary>
    private sealed class CountingFlightSearchProvider : IFlightSearchProvider
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<FlightSearchResult>> SearchAsync(FlightSearchCriteria criteria, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<FlightSearchResult>>([Row]);
        }
    }

    private static readonly FlightSearchResult Row = new(
        FlightNumber: "BA123",
        ServiceDate: new DateOnly(2026, 9, 29),
        DepartureAirportIata: "LHR",
        ArrivalAirportIata: "JFK",
        ScheduledDepartureUtc: new DateTime(2026, 9, 29, 10, 30, 0, DateTimeKind.Utc),
        ScheduledArrivalUtc: new DateTime(2026, 9, 29, 13, 45, 0, DateTimeKind.Utc));

    private static readonly FlightSearchCriteria LhrJfk = new("LHR", "JFK", new DateOnly(2026, 9, 29), null, false);
    private static readonly FlightSearchCriteria LhrCdg = new("LHR", "CDG", new DateOnly(2026, 9, 29), null, false);

    private readonly CountingFlightSearchProvider _inner = new();
    private readonly IProviderBudgetGuard _budget = Substitute.For<IProviderBudgetGuard>();

    private AeroApiFlightSearchBudgetDecorator Decorator() =>
        new(_inner, _budget, NullLogger<AeroApiFlightSearchBudgetDecorator>.Instance);

    private void BudgetIs(ProviderBudgetState state) =>
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(state);

    [Fact]
    public async Task SearchAsync_WithinBudget_CallsTheProvider()
    {
        BudgetIs(ProviderBudgetState.Normal);

        var results = await Decorator().SearchAsync(LhrJfk);

        Assert.Equal(Row, Assert.Single(results));
        Assert.Equal(1, _inner.Calls);
    }

    [Theory]
    [InlineData(ProviderBudgetState.SoftLimit,
        "Flight search is paused: this month's flight data budget is nearly used up. Please try again later.")]
    [InlineData(ProviderBudgetState.Exhausted,
        "Flight search is paused: this month's flight data budget is used up. Please try again later.")]
    public async Task SearchAsync_PastTheSoftLimit_Throws503NamingTheBudget_WithoutCallingTheProvider(
        ProviderBudgetState state, string message)
    {
        BudgetIs(state);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Decorator().SearchAsync(LhrJfk));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(message, ex.Message);
        Assert.Equal(0, _inner.Calls);
    }

    [Fact]
    public async Task SearchAsync_BehindTheCache_ACachedSearchIsStillAnswered_PastTheBudget()
    {
        var search = new AeroApiFlightSearchCachingDecorator(
            Decorator(),
            new MemoryCacheService(new MemoryCache(new MemoryCacheOptions())),
            Options.Create(new AeroApiOptions { ApiKey = "dummy" }),
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<AeroApiFlightSearchCachingDecorator>.Instance);

        BudgetIs(ProviderBudgetState.Normal);
        await search.SearchAsync(LhrJfk);

        BudgetIs(ProviderBudgetState.Exhausted);
        var cached = await search.SearchAsync(LhrJfk);
        var refused = await Assert.ThrowsAsync<DomainException>(() => search.SearchAsync(LhrCdg));

        Assert.Equal(Row, Assert.Single(cached));
        Assert.Equal(503, refused.StatusCode);
        Assert.Equal(1, _inner.Calls);
    }
}
```

- [ ] **Step 2: Write the failing DI chain test** in `AeroApiDependencyInjectionTests.cs`.

(a) Add these usings if they're missing: `System.Net`, `System.Text`, `FlightAssistant.Application.ProviderBudget`, `FlightAssistant.Domain.Common.Exceptions`, `NSubstitute`.

(b) Give `BuildContainer` an optional last parameter, and call it just before the provider is built. The existing callers don't change:

```csharp
    private static ServiceProvider BuildContainer(
        IDictionary<string, string?>? extraSettings = null,
        Action<IServiceCollection>? configure = null)
```

and, directly before `return services.BuildServiceProvider();`:

```csharp
        configure?.Invoke(services);
```

(c) Inside the class, add this nested stub:

```csharp
    /// <summary>Stands in for AeroAPI, answering an empty 200 and counting everything that reaches it.</summary>
    private sealed class CountingTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
```

(d) Directly after `AddInfrastructure_ResolvesIFlightSearchProvider_AsTheCachingDecorator`, add:

```csharp
    [Fact]
    public async Task AddInfrastructure_TheSearchChain_ChecksTheBudgetBetweenTheCacheAndAeroApi()
    {
        // The decorator tests build the chain by hand; this proves the registered one (#303). Past the
        // budget, a cached search is still answered and an uncached one is refused before any request.
        // A counting transport stands in for AeroAPI, so a wrong chain fails here without a real call.
        var transport = new CountingTransport();
        var budget = Substitute.For<IProviderBudgetGuard>();
        using var provider = BuildContainer(configure: services =>
        {
            services.AddScoped(_ => budget);
            services.AddHttpClient("AeroApi").ConfigurePrimaryHttpMessageHandler(() => transport);
        });
        var search = provider.GetRequiredService<IFlightSearchProvider>();
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));

        budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Normal);
        await search.SearchAsync(new FlightSearchCriteria(null, null, date, "BA123", false));

        budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Exhausted);
        var cached = await search.SearchAsync(new FlightSearchCriteria(null, null, date, "BA123", false));
        var refused = await Assert.ThrowsAsync<DomainException>(
            () => search.SearchAsync(new FlightSearchCriteria(null, null, date, "BA124", false)));

        Assert.Empty(cached);
        Assert.Equal(503, refused.StatusCode);
        Assert.Equal(1, transport.Calls);
    }
```

- [ ] **Step 3: Run the tests and watch them fail.**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiFlightSearchBudgetDecoratorTests|FullyQualifiedName~AeroApiDependencyInjectionTests"`
Expected: build FAILS, because `AeroApiFlightSearchBudgetDecorator` doesn't exist. After Step 4 alone, and before Step 5, the DI chain test FAILS: the third search isn't refused, and the transport counts 2 calls.

- [ ] **Step 4: Write the decorator.** Create `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightSearchBudgetDecorator.cs`:

```csharp
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Refuses a search that would reach AeroAPI once this month's spend passes the budget guard's
    /// soft limit (#303), with a 503 that names the budget. It sits inside
    /// <see cref="Caching.AeroApiFlightSearchCachingDecorator"/>, so a cached search is still answered
    /// and only a cache miss is refused. The provider makes its timezone lookups, so a refused search
    /// makes none of those either.
    /// </summary>
    public sealed class AeroApiFlightSearchBudgetDecorator : IFlightSearchProvider
    {
        private const int StatusCode503ServiceUnavailable = 503;

        private readonly IFlightSearchProvider _inner;
        private readonly IProviderBudgetGuard _budget;
        private readonly ILogger<AeroApiFlightSearchBudgetDecorator> _logger;

        public AeroApiFlightSearchBudgetDecorator(
            IFlightSearchProvider inner,
            IProviderBudgetGuard budget,
            ILogger<AeroApiFlightSearchBudgetDecorator> logger)
        {
            _inner = inner;
            _budget = budget;
            _logger = logger;
        }

        public async Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default)
        {
            var state = await _budget.GetStateAsync(ct);
            if (state == ProviderBudgetState.Normal)
                return await _inner.SearchAsync(criteria, ct);

            _logger.LogInformation(
                "Refused an uncached flight search {Origin}-{Destination} {FlightNumber} on {Date}: AeroAPI budget is {State}",
                criteria.DepartureIata, criteria.ArrivalIata, criteria.FlightNumber, criteria.Date, state);

            throw new DomainException(
                state == ProviderBudgetState.Exhausted
                    ? "Flight search is paused: this month's flight data budget is used up. Please try again later."
                    : "Flight search is paused: this month's flight data budget is nearly used up. Please try again later.",
                StatusCode503ServiceUnavailable,
                $"AeroAPI budget state is {state}: searches that aren't cached are refused from the soft limit (#303).");
        }
    }
}
```

- [ ] **Step 5: Put it in the chain.** In `FlightAssistant.Infrastructure/DependencyInjection.cs`, replace the `services.AddScoped<IFlightSearchProvider>(sp => new AeroApiFlightSearchCachingDecorator(` registration's first argument, `sp.GetRequiredService<AeroApiFlightSearchProvider>(),`, with:

```csharp
                // Inside the cache, so a cached search is answered past the budget (#303).
                new AeroApiFlightSearchBudgetDecorator(
                    sp.GetRequiredService<AeroApiFlightSearchProvider>(),
                    sp.GetRequiredService<IProviderBudgetGuard>(),
                    sp.GetRequiredService<ILogger<AeroApiFlightSearchBudgetDecorator>>()),
```

Leave the rest of that registration, and its comment, as it is.

- [ ] **Step 6: Run the tests and watch them pass.**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiFlightSearchBudgetDecoratorTests|FullyQualifiedName~AeroApiDependencyInjectionTests|FullyQualifiedName~AeroApiFlightSearchCachingDecoratorTests"`
Expected: all PASS.

- [ ] **Step 7: Prove the tests.** Make each break, run the named test, see it fail, then restore with `mv … .bak` and `touch`.
1. In `DependencyInjection.cs`, give the caching decorator `sp.GetRequiredService<AeroApiFlightSearchProvider>()` directly again. `AddInfrastructure_TheSearchChain_ChecksTheBudgetBetweenTheCacheAndAeroApi` fails: the uncached search isn't refused.
2. In `DependencyInjection.cs`, put the budget decorator outside the cache (budget → caching → provider). The same test fails: the cached search throws 503.
3. In the decorator, call `_inner.SearchAsync` whatever the state. `SearchAsync_PastTheSoftLimit_Throws503NamingTheBudget_WithoutCallingTheProvider` fails.

- [ ] **Step 8: Update the endpoint descriptions.**

(a) In `FlightAssistant.API/Endpoints/FlightEndpoints.cs`, in the `SearchFlights` description, replace

```csharp
                "- 503: the flight data provider is throttling requests; retry after the period in the Retry-After header.\n" +
```

with

```csharp
                "- 503: the flight data provider is throttling requests, or this month's flight data budget is nearly used up, so only searches already cached are answered; retry after the period in the Retry-After header.\n" +
```

(b) In `FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs`, in the `AdminSearchFlights` description, replace

```csharp
                "search that misses the cache is a paid AeroAPI call, so it shares the flight-search rate limit, counted per admin.")
```

with

```csharp
                "search that misses the cache is a paid AeroAPI call, so it shares the flight-search rate limit, counted per admin. " +
                "Once this month's flight data budget is nearly used up, a search that isn't cached returns 503.")
```

(c) Run `FlightSync__Enabled=false scripts/generate-openapi.sh` from the worktree root. Then run `git diff --stat spec/openapi.json`. Expected: only those two descriptions change.

- [ ] **Step 9: Document it in `CLAUDE.md`.** At the end of the budget guard paragraph Task 1 added, append:

```markdown
 **Search:** `AeroApiFlightSearchBudgetDecorator` sits between `AeroApiFlightSearchCachingDecorator` and `AeroApiFlightSearchProvider`. From `SoftLimit`, a cached search is still answered, and a cache miss gets a 503 naming the budget before any request, including the timezone lookups the search would have made. Phase 3's fallback provider is meant to answer those misses instead.
```

- [ ] **Step 10: Build.** Run `dotnet build --configuration Release`. Expected: 0 errors, 0 warnings.

- [ ] **Step 11: Commit.**

```bash
git add FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightSearchBudgetDecorator.cs \
  FlightAssistant.Infrastructure/DependencyInjection.cs \
  FlightAssistant.API/Endpoints/FlightEndpoints.cs \
  FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs \
  spec/openapi.json \
  CLAUDE.md \
  FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiFlightSearchBudgetDecoratorTests.cs \
  FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiDependencyInjectionTests.cs
git commit -m "feat: past the budget's soft limit, only cached flight searches are answered (#303)

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Status sync slows Live polls, then stops all but Fast

**Files:**
- Modify: `FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs`
- Modify: `FlightAssistant.API/Endpoints/FlightEndpoints.cs` (traveler sync description, `ProducesProblem(503)`)
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs` (admin sync description, `ProducesProblem(503)`)
- Modify: `spec/openapi.json` (regenerated)
- Modify: `CLAUDE.md`
- Test: `FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs`
- Test: `FlightAssistant.API.Tests/HostedServices/FlightStatusSyncBackgroundServiceTests.cs`

**Interfaces:**
- Consumes: `IProviderBudgetGuard` and `ProviderBudgetState` from Task 1.
- Produces: the `SyncFlightStatusCommandHandler` constructor becomes `(IFlightRepository flights, IFlightStatusProvider provider, IProviderBudgetGuard budget, ICacheService cache, IFlightSyncOptions syncOptions, IFlightStatusWindowOptions windowOptions, TimeProvider timeProvider, ILogger<SyncFlightStatusCommandHandler> logger)`.

- [ ] **Step 1: Write the failing handler tests** in `SyncFlightStatusCommandHandlerTests.cs`.

(a) Add `using FlightAssistant.Application.ProviderBudget;`.

(b) Below the `_provider` field, add:

```csharp
    private readonly IProviderBudgetGuard _budget = Substitute.For<IProviderBudgetGuard>();
```

(c) Replace the `Handler(...)` helper with:

```csharp
    private SyncFlightStatusCommandHandler Handler(
        ILogger<SyncFlightStatusCommandHandler>? logger = null, IFlightSyncOptions? syncOptions = null,
        ICacheService? cache = null) =>
        new(_flights, _provider, _budget, cache ?? _cache, syncOptions ?? new SyncOptions(), new WindowOptions(), _clock,
            logger ?? NullLogger<SyncFlightStatusCommandHandler>.Instance);
```

(d) In the constructor, below `_handler = Handler();`, add:

```csharp
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Normal);
```

(e) Add a helper below `Sync(...)`:

```csharp
    private void BudgetIs(ProviderBudgetState state) =>
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(state);
```

(f) At the end of the class, add this section. In these tests the Live interval is 7 min, so twice it is 14. The Fast window is 3 h, and Live starts 1 day before departure.

```csharp
    // ── Budget guard (#303) ──

    [Fact]
    public async Task Live_PastTheSoftLimit_CallsTheProvider_NextSyncAfterTwiceTheLiveInterval()
    {
        var flight = Given(Departing(Now.AddHours(10)));
        BudgetIs(ProviderBudgetState.SoftLimit);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync();

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, StatusCallKind.ScheduledPoll, Arg.Any<CancellationToken>());
        Assert.Equal(Now, flight.LastSyncedUtc);
        Assert.Equal(FlightSyncMode.Live, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(14), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task Live_PastTheSoftLimit_AFailedScheduledSync_WaitsTwiceTheLiveInterval()
    {
        var flight = Given(Departing(Now.AddHours(10)));
        BudgetIs(ProviderBudgetState.SoftLimit);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns<FlightStatusResult?>(_ => throw new DomainException("AeroAPI unavailable", 502));

        await Assert.ThrowsAsync<DomainException>(() => Sync());

        Assert.Null(flight.LastSyncedUtc);
        Assert.Equal(Now.AddMinutes(14), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task Live_BudgetExhausted_ScheduledSync_MakesNoCall_NextSyncAfterTwiceTheLiveInterval()
    {
        var flight = Given(Departing(Now.AddHours(10)));
        BudgetIs(ProviderBudgetState.Exhausted);

        Assert.False(await Sync());

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.Null(flight.LastSyncedUtc);
        Assert.Equal(FlightSyncMode.Live, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(14), flight.NextSyncDueUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Live_BudgetExhausted_ScheduledSync_IsDueAgainNoLaterThanFastStarts()
    {
        // Fast starts in 10 min, before twice the Live interval (14 min).
        var flight = Given(Departing(Now.AddHours(3).AddMinutes(10)));
        BudgetIs(ProviderBudgetState.Exhausted);

        await Sync();

        Assert.Equal(Now.AddMinutes(10), flight.NextSyncDueUtc);
    }

    [Theory]
    [InlineData(SyncTrigger.Admin)]
    [InlineData(SyncTrigger.Traveler)]
    public async Task Live_BudgetExhausted_PersonSync_Throws503_WithoutACallOrAMarker(SyncTrigger trigger)
    {
        var flight = Given(Departing(Now.AddHours(10)));
        BudgetIs(ProviderBudgetState.Exhausted);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Sync(trigger));

        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("budget", ex.Message);
        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        Assert.False(_cache.Entries.ContainsKey(MarkerKey(flight)));
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stopped_BudgetExhausted_PersonSync_Throws503_WithoutACall()
    {
        Given(ArrivedAnHourAgo());
        BudgetIs(ProviderBudgetState.Exhausted);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Sync(SyncTrigger.Admin));

        Assert.Equal(503, ex.StatusCode);
        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fast_BudgetExhausted_StillCallsTheProvider_WithoutReadingTheBudget()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        BudgetIs(ProviderBudgetState.Exhausted);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync();

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, StatusCallKind.ScheduledPoll, Arg.Any<CancellationToken>());
        await _budget.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
        Assert.Equal(Now.AddMinutes(3), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task Schedule_NeverReadsTheBudget()
    {
        Given(Departing(Now.AddDays(5)));

        await Sync();

        await _budget.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
    }
```

- [ ] **Step 2: Write the failing loop test** in `FlightStatusSyncBackgroundServiceTests.cs`.

(a) Add `using FlightAssistant.Application.ProviderBudget;`.

(b) Replace the whole `OverTenMinutes_AFlightNearDeparture_DoesNotSpeedUpTheOthers` test method with this helper and two tests:

```csharp
    /// <summary>
    /// Three tracked flights, all due now, run through the real handler and planner once a minute for
    /// ten minutes. TK1 departs in 3 h (Fast), TK2 in 30 h (Live) and TK3 in 20 days (Schedule).
    /// The intervals are non-default (#308): Fast every 3 min and Live every 20 min, with the Fast
    /// window left at 4 h.
    /// </summary>
    private async Task<(IFlightStatusProvider Provider, Flight[] Flights, int Failed)> RunThreeFlightsForTenMinutesAsync(
        ProviderBudgetState budgetState)
    {
        var options = new FlightSyncOptions { NearDepartureInterval = TimeSpan.FromMinutes(3), Interval = TimeSpan.FromMinutes(20) };
        var fast = WithId(Flight.Create(1, "TK1", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(3), Now.AddHours(6)), 1);
        var live = WithId(Flight.Create(1, "TK2", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(30), Now.AddHours(33)), 2);
        var schedule = WithId(Flight.Create(1, "TK3", 10, 20, DateOnly.FromDateTime(Now.AddDays(20)), Now.AddDays(20), Now.AddDays(20).AddHours(3)), 3);
        var all = new[] { fast, live, schedule };
        foreach (var flight in all)
            flight.ApplySyncPlan(FlightSyncMode.Live, Now); // due now, as the migration backfill leaves them

        _flights.GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>()).Returns(_ => all
            .Where(f => f.NextSyncDueUtc <= _clock.GetUtcNow().UtcDateTime)
            .OrderBy(f => f.NextSyncDueUtc)
            .Select(f => f.Id)
            .ToList());
        _flights.GetFlightByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(ci => all.Single(f => f.Id == ci.ArgAt<int>(0)));
        _flights.IsFlightTrackedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        var provider = Substitute.For<IFlightStatusProvider>();
        provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var window = Substitute.For<IFlightStatusWindowOptions>();
        window.StatusWindowFutureDays.Returns(2);
        var budget = Substitute.For<IProviderBudgetGuard>();
        budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(budgetState);
        var handler = new SyncFlightStatusCommandHandler(_flights, provider, budget, Substitute.For<ICacheService>(), options, window, _clock,
            NullLogger<SyncFlightStatusCommandHandler>.Instance);
        _mediator.Send(Arg.Any<SyncFlightStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.Handle(ci.ArgAt<SyncFlightStatusCommand>(0), CancellationToken.None).GetAwaiter().GetResult());

        var service = Service(options);
        var failed = 0;
        for (var minute = 0; minute < 10; minute++)
        {
            failed += (await service.RunOnceAsync(CancellationToken.None)).Failed;
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        return (provider, all, failed);
    }

    [Fact]
    public async Task OverTenMinutes_AFlightNearDeparture_DoesNotSpeedUpTheOthers()
    {
        var (provider, _, _) = await RunThreeFlightsForTenMinutesAsync(ProviderBudgetState.Normal);

        await provider.Received(4).GetStatusAsync("TK1", Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()); // minutes 0, 3, 6, 9
        await provider.Received(1).GetStatusAsync("TK2", Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()); // minute 0 only
        await provider.DidNotReceive().GetStatusAsync("TK3", Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        // Every call the loop makes is a scheduled poll (#375).
        await provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), StatusCallKind.Interactive, Arg.Any<CancellationToken>());
        Assert.Single(Sent(), c => c.FlightId == 3); // planned once, next due when Live starts
    }

    [Fact]
    public async Task OverTenMinutes_BudgetExhausted_OnlyTheFlightNearDepartureIsCalled()
    {
        var (provider, flights, failed) = await RunThreeFlightsForTenMinutesAsync(ProviderBudgetState.Exhausted);

        await provider.Received(4).GetStatusAsync("TK1", Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>()); // Fast keeps its interval
        await provider.DidNotReceive().GetStatusAsync("TK2", Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        await provider.DidNotReceive().GetStatusAsync("TK3", Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>());
        // TK2 was checked at minute 0 without a call, and waits twice the Live interval.
        Assert.Null(flights[1].LastSyncedUtc);
        Assert.Equal(Now.AddMinutes(40), flights[1].NextSyncDueUtc);
        Assert.Equal(0, failed); // held back, not failed
    }
```

- [ ] **Step 3: Run the tests and watch them fail.**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests"`
Expected: build FAILS, because the handler constructor has no `IProviderBudgetGuard` parameter.

- [ ] **Step 4: Write the handler change** in `SyncFlightStatusCommand.cs`.

(a) Add `using FlightAssistant.Application.ProviderBudget;`.

(b) Replace the command's summary with:

```csharp
    /// <summary>
    /// Syncs one flight when its sync mode calls for it, then stores when it's next due (#356). Holds
    /// back calls as this month's AeroAPI spend nears the budget (#303).
    /// </summary>
```

(c) Add a field below `_provider`:

```csharp
        private readonly IProviderBudgetGuard _budget;
```

(d) Add the constructor parameter `IProviderBudgetGuard budget,` directly after `IFlightStatusProvider provider,`, and assign `_budget = budget;` directly after `_provider = provider;`.

(e) Replace

```csharp
            if (travelerCooldown)
                await MarkTravelerAttemptAsync(flight.Id, now, ct);
```

with

```csharp
            // #303: a flight in Fast mode always gets its call and never reads the budget. Past the soft
            // limit, a Live flight's polls run at twice the interval. Once the budget is used up, the loop
            // makes no call for it, and a person's sync is told why rather than handed stored status.
            var budget = plan.Mode == FlightSyncMode.Fast
                ? ProviderBudgetState.Normal
                : await _budget.GetStateAsync(ct);
            if (budget != ProviderBudgetState.Normal)
            {
                options = options with { LiveInterval = options.LiveInterval * 2 };
                plan = FlightSyncPlanner.Plan(flight, isTracked, now, options);
            }

            if (budget == ProviderBudgetState.Exhausted)
            {
                if (cmd.Trigger != SyncTrigger.Scheduled)
                    throw new DomainException(
                        "Flight status can't be refreshed right now: this month's flight data budget is used up. Please try again later.",
                        503);

                flight.ApplySyncPlan(plan.Mode, plan.NextSyncDueUtc);
                await _flights.SaveChangesAsync(ct);
                return false;
            }

            if (travelerCooldown)
                await MarkTravelerAttemptAsync(flight.Id, now, ct);
```

`options` and `plan` are the existing `var` locals declared above the `switch`. Keep them as `var`, so reassigning them compiles.

- [ ] **Step 5: Run the tests and watch them pass.**

Run:
- `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests"`
- `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightStatusSyncBackgroundServiceTests|FullyQualifiedName~FlightEndpointTests|FullyQualifiedName~AdminFlightEndpointTests"`

Expected: all PASS. The API endpoint tests resolve the handler through the real host, which proves `IProviderBudgetGuard` is registered. They sync Schedule-mode and untracked flights, so they never read the budget.

- [ ] **Step 6: Prove the tests.** Make each break, run the named tests, see them fail, then restore with `mv … .bak` and `touch`.
1. Delete the `options = options with { … }` and `plan = …` re-plan. `Live_PastTheSoftLimit_CallsTheProvider_NextSyncAfterTwiceTheLiveInterval`, `Live_PastTheSoftLimit_AFailedScheduledSync_WaitsTwiceTheLiveInterval` and `Live_BudgetExhausted_ScheduledSync_MakesNoCall_NextSyncAfterTwiceTheLiveInterval` fail (7 min, not 14).
2. Always read the budget: `var budget = await _budget.GetStateAsync(ct);`. `Fast_BudgetExhausted_StillCallsTheProvider_WithoutReadingTheBudget` fails.
3. Delete the whole `if (budget == ProviderBudgetState.Exhausted) { … }` block. `Live_BudgetExhausted_ScheduledSync_MakesNoCall_NextSyncAfterTwiceTheLiveInterval`, `Live_BudgetExhausted_PersonSync_Throws503_WithoutACallOrAMarker` and `OverTenMinutes_BudgetExhausted_OnlyTheFlightNearDepartureIsCalled` fail.
4. Throw the 503 for every trigger (drop `if (cmd.Trigger != SyncTrigger.Scheduled)`). `Live_BudgetExhausted_ScheduledSync_MakesNoCall_NextSyncAfterTwiceTheLiveInterval` fails with a `DomainException`, and `OverTenMinutes_BudgetExhausted_OnlyTheFlightNearDepartureIsCalled` fails on `failed`.
5. Move the `if (travelerCooldown) await MarkTravelerAttemptAsync(...)` lines above the budget block. `Live_BudgetExhausted_PersonSync_Throws503_WithoutACallOrAMarker(trigger: Traveler)` fails on the marker.

- [ ] **Step 7: Update the endpoint descriptions.**

(a) In `FlightAssistant.API/Endpoints/FlightEndpoints.cs`, in the `SyncFlightStatus` description, replace

```csharp
                "A flight synced within the manual-sync cooldown (5 minutes by default) makes no call either. " +
```

with

```csharp
                "A flight synced within the manual-sync cooldown (5 minutes by default) makes no call either. " +
                "Once this month's flight data budget is used up, only a flight in its near-departure window (from 4 hours before departure, by default) is refreshed; any other returns 503. " +
```

Then add `.ProducesProblem(503)` directly after that endpoint's `.ProducesProblem(404)`.

(b) In `FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs`, in the `AdminSyncFlightStatus` description, replace

```csharp
                "Otherwise returns { updated: true } if a new status was recorded.")
            .Produces(200);
```

with

```csharp
                "Otherwise returns { updated: true } if a new status was recorded. " +
                "Once this month's flight data budget is used up, only a flight in its near-departure window makes a call; any other returns 503.")
            .Produces(200)
            .ProducesProblem(503);
```

(c) Run `FlightSync__Enabled=false scripts/generate-openapi.sh` from the worktree root. Then run `git diff --stat spec/openapi.json`. Expected: only the two sync operations change, each with its description and a 503 response.

- [ ] **Step 8: Document it in `CLAUDE.md`.** At the end of the budget guard paragraph, after Task 2's search sentence, append:

```markdown
 **Status sync:** `SyncFlightStatusCommandHandler` never reads the budget for a flight in Fast mode, which always gets its call. From `SoftLimit`, a Live flight's next sync is planned at twice `FlightSync:Interval`. At `Exhausted`, the background loop makes no call for a flight that isn't in Fast mode and plans it again at that doubled interval (capped at when Fast starts), without counting it as a failure. A person's sync of such a flight gets a 503 naming the budget rather than a silent `{ updated: false }`, since the stored status can be an hour old.
```

- [ ] **Step 9: Build and run everything.**
- Run `dotnet build --configuration Release`. Expected: 0 errors, 0 warnings.
- Run the full `dotnet test` once (Docker is running) and record the per-project counts.

- [ ] **Step 10: Commit.**

```bash
git add FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs \
  FlightAssistant.API/Endpoints/FlightEndpoints.cs \
  FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs \
  spec/openapi.json \
  CLAUDE.md \
  FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs \
  FlightAssistant.API.Tests/HostedServices/FlightStatusSyncBackgroundServiceTests.cs
git commit -m "feat: past the budget's soft limit Live polls slow down, and once it's used up only Fast flights are synced (#303)

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```
