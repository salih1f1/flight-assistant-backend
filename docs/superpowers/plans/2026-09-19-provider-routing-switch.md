# Provider Routing Switch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make four provider-routing decisions changeable at runtime from the admin panel, audited and reversible without a deploy, and publish one endpoint that says which provider is called on which operation.

**Architecture:** A `provider_routes` table read through a 10-second cache, mirroring the existing `JobSwitch` mechanism (#471) including its configuration ceiling and its explicitly-written audit row. The routing decision for flight search moves into one `IFlightSearchRouter` that both the provider selector and the search cache read, so the cache key can gain a provider dimension without a second copy of the routing rule. Verification defaults move behind `IVerificationSourceDefaults`, which reads the switch and falls back to configuration.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core + PostgreSQL (Npgsql), MediatR, FluentValidation, xUnit, Moq, Microsoft.Extensions.TimeProvider.Testing (`FakeTimeProvider`).

**Spec:** `docs/superpowers/specs/2026-09-19-provider-routing-switch-design.md`

## Global Constraints

- Branch off `development`. Never commit to `development` or `main`. Branch name `feature/{ISSUE}-{short-description}`.
- **Three PRs, three issues.** PR 1 is issue #531. PR 2 and PR 3 each get their own GitHub issue, created when that PR starts, and their own branch off `development`.
- Table names are `snake_case`. All timestamps UTC only, suffixed `Utc`, stored as `timestamptz`.
- `SaveChangesAsync()` is called explicitly inside handlers. No implicit unit of work.
- Commands and queries live one-per-file with their validator and handler: `Application/Commands/{Domain}/{CommandName}.cs`.
- Result/DTO types are named `{Name}Result` or `{Name}Dto`. Endpoint tag group names match the bounded context.
- A format rule shared by more than one validator lives in **one** constant, never two copies of a regex.
- `dotnet test --filter` accepts **only** `FullyQualifiedName~`. `ClassName=` and `Name~` match nothing, run zero tests, and report no failure — a filtered run with either form looks exactly like a passing run.
- Never declare both `.ProducesValidationProblem()` and `.ProducesProblem(400)` on one route: OpenAPI allows one entry per status, the later wins, and the `errors` dictionary is silently lost.
- `.Produces*` lists only statuses **this operation** can produce. Never declare 401, 403, or 429.
- Regenerate `spec/openapi.json` with `scripts/generate-openapi.sh` from the repo root in every PR that touches a route, or CI fails the build byte-for-byte.
- PostgreSQL-backed tests need Docker running (Testcontainers harness, #295).

---

## File Structure

**PR 1 — switch mechanism, route search, cache key**

| File | Responsibility |
|---|---|
| `FlightAssistant.Domain/Providers/Entities/ProviderRoute.cs` | The persisted row. Mutable properties, like `JobSwitch`. |
| `FlightAssistant.Application/Providers/ProviderJobs.cs` | The closed key list and each key's configured-default reader name. |
| `FlightAssistant.Application/Providers/ProviderRouteState.cs` | The read model returned by `IProviderRouting.GetAsync`. |
| `FlightAssistant.Application/Interfaces/Providers/IProviderRouting.cs` | Cached read side. |
| `FlightAssistant.Application/Interfaces/Providers/IProviderRouteRepository.cs` | Uncached write side. |
| `FlightAssistant.Infrastructure/Providers/CachedProviderRouting.cs` | `IProviderRouting`, 10 s cache, configuration fallback. |
| `FlightAssistant.Infrastructure/Persistance/Repositories/ProviderRouteRepository.cs` | Upsert. |
| `FlightAssistant.Infrastructure/Persistance/Configurations/Providers/ProviderRouteConfiguration.cs` | Table mapping. |
| `FlightAssistant.Infrastructure/ExternalServices/IFlightSearchRouter.cs` | The one routing rule, two readers. |
| `FlightAssistant.Infrastructure/ExternalServices/FlightSearchRouter.cs` | Implementation. |
| `FlightAssistant.Application/Commands/Admin/Providers/SetProviderRouteCommand.cs` | Command, validator, handler, explicit audit row. |
| `FlightAssistant.Application/Queries/Admin/Providers/GetProviderRoutesQuery.cs` | Read query. |
| `FlightAssistant.Contracts/Requests/Admin/SetProviderRouteRequest.cs` | Request body. |
| `FlightAssistant.Contracts/Responses/Admin/AdminProviderRouteDto.cs` | Response row. |
| `FlightAssistant.API/Endpoints/Admin/AdminProviderEndpoints.cs` | `GET`/`PUT /admin/providers`. |

Modified: `FlightAssistantDbContext` (DbSet), `BrowsableTables` (registry entry), `AdminAuditInterceptor` (`NotAuditedTypes`), `FlightSearchProviderSelector`, `AeroApiFlightSearchCachingDecorator`, `DependencyInjection`, `Program.cs` (map the group).

**PR 2 — verification defaults**

| File | Responsibility |
|---|---|
| `FlightAssistant.Application/Interfaces/Verification/IVerificationSourceDefaults.cs` | Switch-aware default sources. |
| `FlightAssistant.Application/Verification/VerificationSourceDefaults.cs` | Implementation over `IProviderRouting` + `IVerificationJobOptions`. |

Modified: `FlightTrackedVerificationEventHandler`, `ReferenceDataProvisioner`, `SetProviderRouteCommand` (registry validation), `ProviderJobs` (three more keys).

**PR 3 — map and headroom**

| File | Responsibility |
|---|---|
| `FlightAssistant.Infrastructure/ExternalServices/AeroDataBox/AeroDataBoxQuotaHandler.cs` | Reads `x-ratelimit-*` off responses. |
| `FlightAssistant.Infrastructure/ExternalServices/AeroDataBox/AeroDataBoxQuotaHolder.cs` | Singleton last-seen quota. |
| `FlightAssistant.Application/Interfaces/Providers/IProviderQuotaReader.cs` | Application's view of that holder. |
| `FlightAssistant.Application/Providers/ProviderOperationMap.cs` | The authored trigger→provider registry. |
| `FlightAssistant.Contracts/Responses/Admin/AdminProviderMapDto.cs` | Map row + headroom DTOs. |

Modified: `GetProviderRoutesQuery` (returns the map), `AdminProviderEndpoints`.

---

# PR 1 — switch mechanism, route search, cache key (issue #531)

### Task 1: The `ProviderRoute` row and its registry

**Files:**
- Create: `FlightAssistant.Domain/Providers/Entities/ProviderRoute.cs`
- Create: `FlightAssistant.Application/Providers/ProviderJobs.cs`
- Modify: `FlightAssistant.Infrastructure/Persistance/FlightAssistantDbContext.cs`
- Create: `FlightAssistant.Infrastructure/Persistance/Configurations/Providers/ProviderRouteConfiguration.cs`
- Modify: `FlightAssistant.Application/Administration/BrowsableTables.cs`
- Modify: `FlightAssistant.Infrastructure/Persistance/AdminAuditInterceptor.cs`
- Test: `FlightAssistant.Infrastructure.Tests/Administration/BrowsableTablesModelCompletenessTests.cs` (existing, must stay green)

**Interfaces:**
- Consumes: nothing.
- Produces: `ProviderRoute` with mutable `Job`, `Providers`, `ChangedUtc`, `ChangedBy`, `Reason`. `ProviderJobs.FlightSearchRoute` = `"flight-search-route"`, `ProviderJobs.All`, `ProviderJobs.DisplayNameFor(string)`, `ProviderJobs.OptionsFor(string)`.

- [ ] **Step 1: Create the entity**

```csharp
namespace FlightAssistant.Domain.Providers.Entities
{
    /// <summary>
    /// One routing decision's runtime value. Persisted rather than held in memory so a change
    /// survives a restart, for the same reason <c>JobSwitch</c> is: a deploy silently reverting a
    /// routing decision an operator made is the failure they would least expect and least quickly
    /// notice.
    ///
    /// <para>
    /// A sibling of <c>job_switches</c> rather than a widening of it. That table's value is a
    /// boolean and <c>JobNames</c> states its closed list is what keeps it "two booleans with a
    /// config ceiling, not a feature-flag framework"; adding a nullable string column to carry a
    /// value would weaken both. Each registry stays closed on its own terms.
    /// </para>
    /// </summary>
    public sealed class ProviderRoute
    {
        public string Job { get; set; } = string.Empty;

        /// <summary>
        /// The chosen provider or source names, comma-joined in the order they are asked — the same
        /// grammar <c>VerificationDefaultSources</c> uses, validated by
        /// <c>VerificationRequest.TryJoinSources</c>. <c>flight-search-route</c> happens to hold a
        /// single name; nothing in this shape assumes it.
        /// </summary>
        public string Providers { get; set; } = string.Empty;

        public DateTime? ChangedUtc { get; set; }

        public string? ChangedBy { get; set; }

        /// <summary>Optional operator note, rendered beside the state and folded into the audit row.</summary>
        public string? Reason { get; set; }
    }
}
```

- [ ] **Step 2: Create the key registry**

```csharp
namespace FlightAssistant.Application.Providers
{
    /// <summary>
    /// The routing decisions an admin can change. A closed list, for the reason <see cref="Jobs.JobNames"/>
    /// gives for its own: this is a small set of deliberate choices, not a feature-flag framework,
    /// and a closed list is what keeps it from becoming one. Adding a key means adding a reader for
    /// it, which is the point.
    /// </summary>
    public static class ProviderJobs
    {
        public const string FlightSearchRoute = "flight-search-route";

        public const string AeroApi = "aeroapi";
        public const string AeroDataBox = "aerodatabox";

        public static readonly IReadOnlyList<string> All = [FlightSearchRoute];

        public static string DisplayNameFor(string job) => job switch
        {
            FlightSearchRoute => "Route search",
            _ => throw Unknown(job)
        };

        /// <summary>
        /// The provider names this key accepts. For verification keys (PR 2) the real answer comes
        /// from <c>IVerificationSourceRegistry</c> instead, since it depends on which sources are
        /// registered and which entity types they support.
        /// </summary>
        public static IReadOnlyList<string> OptionsFor(string job) => job switch
        {
            FlightSearchRoute => [AeroDataBox, AeroApi],
            _ => throw Unknown(job)
        };

        private static InvalidOperationException Unknown(string job) =>
            new($"'{job}' is not a known provider routing key.");
    }
}
```

- [ ] **Step 3: Map the table**

```csharp
using FlightAssistant.Domain.Providers.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Providers
{
    /// <summary>The <c>provider_routes</c> table: one row per routing key. snake_case columns,
    /// matching <c>job_switches</c> beside it.</summary>
    public sealed class ProviderRouteConfiguration : IEntityTypeConfiguration<ProviderRoute>
    {
        public void Configure(EntityTypeBuilder<ProviderRoute> b)
        {
            b.ToTable("provider_routes");

            // The key name is the natural key: exactly one row per decision, every read and write
            // addresses it by name.
            b.HasKey(x => x.Job);

            b.Property(x => x.Job).HasColumnName("job").HasMaxLength(64);
            b.Property(x => x.Providers).HasColumnName("providers").HasMaxLength(256);
            b.Property(x => x.ChangedUtc).HasColumnName("changed_utc");
            b.Property(x => x.ChangedBy).HasColumnName("changed_by").HasMaxLength(256);
            b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(512);
        }
    }
}
```

- [ ] **Step 4: Add the DbSet**

In `FlightAssistantDbContext`, directly after the `JobSwitches` set:

```csharp
        /// <summary>#531. One row per routing decision an admin has changed; no row means the
        /// configured default applies.</summary>
        public DbSet<ProviderRoute> ProviderRoutes => Set<ProviderRoute>();
```

Add `using FlightAssistant.Domain.Providers.Entities;` at the top.

- [ ] **Step 5: Exclude it from the audit interceptor**

In `AdminAuditInterceptor`, extend the comment block above `NotAuditedTypes` with:

```csharp
        // - ProviderRoute (#531): excluded for exactly the reason JobSwitch above is. Written rarely
        //   and deliberately, and audited EXPLICITLY by SetProviderRouteCommandHandler so the
        //   operator's reason text can be carried, which this interceptor's generic before/after diff
        //   has no field for. Letting both fire would put two rows in the log for one change.
```

and add `typeof(ProviderRoute)` to the set.

- [ ] **Step 6: Register it in `BrowsableTables`**

Directly after the `job-switches` entry:

```csharp
            // #531. Changed through the dedicated admin endpoint (audited explicitly there, see
            // AdminAuditInterceptor's ProviderRoute exclusion) — browsable all the same, following
            // this registry's "default to including things" rule. Nothing here is sensitive:
            // ChangedBy is an admin id, and Providers is a source name.
            new("provider-routes", typeof(ProviderRoute), "Provider Routes", new[]
            {
                nameof(ProviderRoute.Job),
                nameof(ProviderRoute.Providers),
                nameof(ProviderRoute.ChangedUtc),
                nameof(ProviderRoute.ChangedBy),
                nameof(ProviderRoute.Reason),
            }),
```

- [ ] **Step 7: Run the completeness guard to verify it is satisfied**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~BrowsableTablesModelCompletenessTests"`
Expected: PASS. This guard fails when a `DbSet` is unclassified — it is the reason Step 6 is in this task and not a later one.

- [ ] **Step 8: Create the migration**

```bash
dotnet ef migrations add AddProviderRoutes \
  --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
```

- [ ] **Step 9: Verify the migration and build**

Run: `dotnet build --configuration Release`
Expected: build succeeds. Open the generated migration and confirm it creates `provider_routes` with `job` as the primary key and no other table touched.

- [ ] **Step 10: Commit**

```bash
git add FlightAssistant.Domain/Providers FlightAssistant.Application/Providers \
  FlightAssistant.Infrastructure/Persistance FlightAssistant.Application/Administration/BrowsableTables.cs
git commit -m "feat(providers): add the provider_routes row, registry and migration (#531)"
```

---

### Task 2: The cached read side

**Files:**
- Create: `FlightAssistant.Application/Providers/ProviderRouteState.cs`
- Create: `FlightAssistant.Application/Interfaces/Providers/IProviderRouting.cs`
- Create: `FlightAssistant.Infrastructure/Providers/CachedProviderRouting.cs`
- Test: `FlightAssistant.Infrastructure.Tests/Providers/CachedProviderRoutingTests.cs`

**Interfaces:**
- Consumes: `ProviderJobs`, `ProviderRoute` (Task 1).
- Produces: `IProviderRouting.GetProvidersAsync(string job, CancellationToken ct) → Task<IReadOnlyList<string>>` (cached, falls back to the configured default) and `IProviderRouting.GetAsync(string job, CancellationToken ct) → Task<ProviderRouteState>` (uncached). `ProviderRouteState(string Job, IReadOnlyList<string> Providers, bool IsDefault, DateTime? ChangedUtc, string? ChangedBy, string? Reason)`. `CachedProviderRouting.CacheWindow` = 10 s. Internal delegate `ProviderRouteReader(string job, CancellationToken ct) → Task<ProviderRouteState?>` is the test seam.

- [ ] **Step 1: Create the read model**

```csharp
namespace FlightAssistant.Application.Providers
{
    /// <param name="IsDefault">True when no row exists and these providers came from configuration.
    /// The panel renders "default" rather than implying an operator chose it.</param>
    public sealed record ProviderRouteState(
        string Job,
        IReadOnlyList<string> Providers,
        bool IsDefault,
        DateTime? ChangedUtc,
        string? ChangedBy,
        string? Reason);
}
```

- [ ] **Step 2: Create the interface**

```csharp
using FlightAssistant.Application.Providers;

namespace FlightAssistant.Application.Interfaces.Providers
{
    /// <summary>
    /// The runtime routing value for one decision, read on every search and every verification
    /// enqueue.
    ///
    /// <para>
    /// Answers the switch only, never the configuration ceiling: whether the deployment can reach
    /// the chosen provider at all is a separate question its caller combines in, exactly as
    /// <c>IJobSwitch</c> keeps <c>FlightSync:Enabled</c> out of its own answer.
    /// </para>
    /// </summary>
    public interface IProviderRouting
    {
        /// <summary>
        /// The chosen providers in ask order, cached — a per-request read of a value that changes
        /// maybe twice a month would otherwise be a database round trip per search. Falls back to
        /// the configured default when no row exists, and also when the read fails.
        /// </summary>
        Task<IReadOnlyList<string>> GetProvidersAsync(string job, CancellationToken ct);

        /// <summary>The full row, for the admin endpoint. Uncached — an operator reading the page
        /// after pressing the button must see what they just did, not a stale cache entry.</summary>
        Task<ProviderRouteState> GetAsync(string job, CancellationToken ct);
    }
}
```

- [ ] **Step 3: Write the failing tests**

Create `FlightAssistant.Infrastructure.Tests/Providers/CachedProviderRoutingTests.cs`:

```csharp
using FlightAssistant.Application.Providers;
using FlightAssistant.Infrastructure.Providers;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightAssistant.Infrastructure.Tests.Providers
{
    public class CachedProviderRoutingTests
    {
        private static readonly IReadOnlyList<string> ConfiguredDefault = new[] { "aerodatabox" };

        private static CachedProviderRouting Build(
            ProviderRouteReader read,
            FakeTimeProvider time) =>
            new(read, _ => ConfiguredDefault, time, NullLogger<CachedProviderRouting>.Instance);

        [Fact]
        public async Task GetProvidersAsync_NoRow_ReturnsTheConfiguredDefault()
        {
            var time = new FakeTimeProvider();
            var routing = Build((_, _) => Task.FromResult<ProviderRouteState?>(null), time);

            var providers = await routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, default);

            Assert.Equal(new[] { "aerodatabox" }, providers);
        }

        [Fact]
        public async Task GetProvidersAsync_ReadsOnceInsideTheCacheWindow()
        {
            var time = new FakeTimeProvider();
            var reads = 0;
            var routing = Build((job, _) =>
            {
                reads++;
                return Task.FromResult<ProviderRouteState?>(
                    new ProviderRouteState(job, new[] { "aeroapi" }, false, null, null, null));
            }, time);

            await routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, default);
            time.Advance(CachedProviderRouting.CacheWindow - TimeSpan.FromMilliseconds(1));
            var second = await routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, default);

            Assert.Equal(1, reads);
            Assert.Equal(new[] { "aeroapi" }, second);
        }

        [Fact]
        public async Task GetProvidersAsync_ReadsAgainOnceTheWindowHasPassed()
        {
            var time = new FakeTimeProvider();
            var reads = 0;
            var routing = Build((job, _) =>
            {
                reads++;
                return Task.FromResult<ProviderRouteState?>(
                    new ProviderRouteState(job, new[] { "aeroapi" }, false, null, null, null));
            }, time);

            await routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, default);
            time.Advance(CachedProviderRouting.CacheWindow);
            await routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, default);

            Assert.Equal(2, reads);
        }

        [Fact]
        public async Task GetProvidersAsync_ReadFailure_FallsBackToTheConfiguredDefault()
        {
            // Unlike CachedJobSwitch, there is no fail-open/fail-closed split to make: a routing
            // read failure has one unambiguous right answer, because configuration is always
            // available in-process.
            var time = new FakeTimeProvider();
            var routing = Build((_, _) => throw new InvalidOperationException("no database"), time);

            var providers = await routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, default);

            Assert.Equal(new[] { "aerodatabox" }, providers);
        }

        [Fact]
        public async Task GetAsync_NoRow_ReportsTheDefaultAsDefault()
        {
            var time = new FakeTimeProvider();
            var routing = Build((_, _) => Task.FromResult<ProviderRouteState?>(null), time);

            var state = await routing.GetAsync(ProviderJobs.FlightSearchRoute, default);

            Assert.True(state.IsDefault);
            Assert.Equal(new[] { "aerodatabox" }, state.Providers);
            Assert.Null(state.ChangedBy);
        }

        [Fact]
        public async Task GetAsync_IsNotCached()
        {
            var time = new FakeTimeProvider();
            var reads = 0;
            var routing = Build((job, _) =>
            {
                reads++;
                return Task.FromResult<ProviderRouteState?>(
                    new ProviderRouteState(job, new[] { "aeroapi" }, false, null, null, null));
            }, time);

            await routing.GetAsync(ProviderJobs.FlightSearchRoute, default);
            await routing.GetAsync(ProviderJobs.FlightSearchRoute, default);

            Assert.Equal(2, reads);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~CachedProviderRoutingTests"`
Expected: FAIL to compile — `CachedProviderRouting` and `ProviderRouteReader` do not exist.

- [ ] **Step 5: Implement `CachedProviderRouting`**

```csharp
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
```

- [ ] **Step 6: Create `IProviderRouteDefaults`, the configured-default seam**

Create `FlightAssistant.Application/Interfaces/Providers/IProviderRouteDefaults.cs`:

```csharp
namespace FlightAssistant.Application.Interfaces.Providers
{
    /// <summary>
    /// The configured default for a routing key — what applies when no row exists and what a failed
    /// read falls back to. An interface rather than an <c>IConfiguration</c> read inside
    /// <c>CachedProviderRouting</c> because each key's default comes from a different place:
    /// <c>flight-search-route</c> from whether <c>AeroDataBox:ApiKey</c> is set, the verification
    /// keys (PR 2) from <c>Verification:DefaultSources:*</c>.
    /// </summary>
    public interface IProviderRouteDefaults
    {
        IReadOnlyList<string> For(string job);
    }
}
```

Create `FlightAssistant.Infrastructure/Providers/ProviderRouteDefaults.cs`:

```csharp
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Providers;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Providers
{
    /// <inheritdoc/>
    public sealed class ProviderRouteDefaults : IProviderRouteDefaults
    {
        private readonly IOptions<AeroDataBoxOptions> _aeroDataBox;

        public ProviderRouteDefaults(IOptions<AeroDataBoxOptions> aeroDataBox) => _aeroDataBox = aeroDataBox;

        public IReadOnlyList<string> For(string job) => job switch
        {
            // Exactly the pre-#531 behaviour: route search went to AeroDataBox when its key was
            // configured and to AeroAPI otherwise. With no row, nothing changes.
            ProviderJobs.FlightSearchRoute =>
                _aeroDataBox.Value.IsConfigured ? [ProviderJobs.AeroDataBox] : [ProviderJobs.AeroApi],
            _ => throw new InvalidOperationException($"'{job}' is not a known provider routing key.")
        };
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~CachedProviderRoutingTests"`
Expected: PASS, 6 tests.

- [ ] **Step 8: Commit**

```bash
git add FlightAssistant.Application/Providers FlightAssistant.Application/Interfaces/Providers \
  FlightAssistant.Infrastructure/Providers FlightAssistant.Infrastructure.Tests/Providers
git commit -m "feat(providers): cached routing read with a configured-default fallback (#531)"
```

---

### Task 3: The write side

**Files:**
- Create: `FlightAssistant.Application/Interfaces/Providers/IProviderRouteRepository.cs`
- Create: `FlightAssistant.Infrastructure/Persistance/Repositories/ProviderRouteRepository.cs`
- Test: `FlightAssistant.Infrastructure.Tests/Providers/ProviderRouteRepositoryPostgresTests.cs`

**Interfaces:**
- Consumes: `ProviderRoute` (Task 1).
- Produces: `IProviderRouteRepository.UpsertAsync(ProviderRoute row, CancellationToken ct) → Task`.

- [ ] **Step 1: Create the interface**

```csharp
using FlightAssistant.Domain.Providers.Entities;

namespace FlightAssistant.Application.Interfaces.Providers
{
    /// <summary>
    /// Write side for <c>provider_routes</c>. Kept apart from <see cref="IProviderRouting"/>'s
    /// cached read side so a write can never inherit that policy: a write must always reach the
    /// database, never be served from a cache entry, and never silently "succeed" against a
    /// database it did not reach. Same split, same reason, as <c>IJobSwitchRepository</c>.
    /// </summary>
    public interface IProviderRouteRepository
    {
        /// <summary>Inserts or overwrites the row for <c>row.Job</c>.</summary>
        Task UpsertAsync(ProviderRoute row, CancellationToken ct);
    }
}
```

- [ ] **Step 2: Write the failing PostgreSQL test**

Create `FlightAssistant.Infrastructure.Tests/Providers/ProviderRouteRepositoryPostgresTests.cs`. Follow the existing harness pattern in `FlightAssistant.Infrastructure.Tests` (see `VerificationRequestRepositoryPostgresTests` for how the fixture is taken as a constructor parameter and how a context is created).

```csharp
using FlightAssistant.Domain.Providers.Entities;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Providers
{
    // PostgreSQL-backed because the InMemory provider would not exercise the real key constraint
    // that makes the second call an update rather than a duplicate-row insert.
    public class ProviderRouteRepositoryPostgresTests : PostgresTestBase
    {
        public ProviderRouteRepositoryPostgresTests(PostgresFixture fixture) : base(fixture) { }

        [Fact]
        public async Task UpsertAsync_NoExistingRow_Inserts()
        {
            await using var db = CreateContext();
            var repository = new ProviderRouteRepository(db);

            await repository.UpsertAsync(new ProviderRoute
            {
                Job = "flight-search-route",
                Providers = "aeroapi",
                ChangedUtc = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc),
                ChangedBy = "admin-1",
                Reason = "AeroDataBox rows looked wrong",
            }, default);

            var stored = await db.ProviderRoutes.AsNoTracking()
                .SingleAsync(r => r.Job == "flight-search-route");
            Assert.Equal("aeroapi", stored.Providers);
            Assert.Equal("admin-1", stored.ChangedBy);
        }

        [Fact]
        public async Task UpsertAsync_ExistingRow_OverwritesItRatherThanAddingASecond()
        {
            await using var db = CreateContext();
            var repository = new ProviderRouteRepository(db);

            await repository.UpsertAsync(new ProviderRoute
            {
                Job = "flight-search-route", Providers = "aeroapi", ChangedBy = "admin-1",
            }, default);
            await repository.UpsertAsync(new ProviderRoute
            {
                Job = "flight-search-route", Providers = "aerodatabox", ChangedBy = "admin-2",
                Reason = "quota restored",
            }, default);

            var rows = await db.ProviderRoutes.AsNoTracking()
                .Where(r => r.Job == "flight-search-route").ToListAsync();
            Assert.Single(rows);
            Assert.Equal("aerodatabox", rows[0].Providers);
            Assert.Equal("admin-2", rows[0].ChangedBy);
            Assert.Equal("quota restored", rows[0].Reason);
        }
    }
}
```

Note: if the existing harness exposes its fixture differently (base class name, context factory method), match the existing file exactly rather than this sketch — read `VerificationRequestRepositoryPostgresTests` first and copy its wiring.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ProviderRouteRepositoryPostgresTests"`
Expected: FAIL to compile — `ProviderRouteRepository` does not exist. Docker must be running.

- [ ] **Step 4: Implement the repository**

```csharp
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Domain.Providers.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    /// <summary>Upserts <c>provider_routes</c>. One row per key — four at most — so no paging or
    /// filtering is needed.</summary>
    public sealed class ProviderRouteRepository : IProviderRouteRepository
    {
        private readonly FlightAssistantDbContext _db;

        public ProviderRouteRepository(FlightAssistantDbContext db) => _db = db;

        public async Task UpsertAsync(ProviderRoute row, CancellationToken ct)
        {
            var existing = await _db.ProviderRoutes.FirstOrDefaultAsync(r => r.Job == row.Job, ct);

            if (existing is null)
            {
                _db.ProviderRoutes.Add(row);
            }
            else
            {
                existing.Providers = row.Providers;
                existing.ChangedUtc = row.ChangedUtc;
                existing.ChangedBy = row.ChangedBy;
                existing.Reason = row.Reason;
            }

            await _db.SaveChangesAsync(ct);
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ProviderRouteRepositoryPostgresTests"`
Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Application/Interfaces/Providers/IProviderRouteRepository.cs \
  FlightAssistant.Infrastructure/Persistance/Repositories/ProviderRouteRepository.cs \
  FlightAssistant.Infrastructure.Tests/Providers/ProviderRouteRepositoryPostgresTests.cs
git commit -m "feat(providers): upsert provider_routes (#531)"
```

---

### Task 4: One routing rule, read by the selector

**Files:**
- Create: `FlightAssistant.Infrastructure/ExternalServices/IFlightSearchRouter.cs`
- Create: `FlightAssistant.Infrastructure/ExternalServices/FlightSearchRouter.cs`
- Modify: `FlightAssistant.Infrastructure/ExternalServices/FlightSearchProviderSelector.cs`
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs:346-360`
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/FlightSearchProviderSelectorTests.cs` (existing — extend)

**Interfaces:**
- Consumes: `IProviderRouting.GetProvidersAsync` (Task 2), `ProviderJobs` (Task 1).
- Produces: `IFlightSearchRouter.ResolveAsync(FlightSearchCriteria criteria, CancellationToken ct) → Task<string>` returning `ProviderJobs.AeroDataBox` or `ProviderJobs.AeroApi`.

- [ ] **Step 1: Create the interface**

```csharp
using FlightAssistant.Application.Interfaces.ExternalServices;

namespace FlightAssistant.Infrastructure.ExternalServices
{
    /// <summary>
    /// Which provider a given search goes to. Extracted from <see cref="FlightSearchProviderSelector"/>
    /// (#531) so there is exactly one copy of the rule and two readers of it: the selector, which
    /// routes the call, and <c>AeroApiFlightSearchCachingDecorator</c>, which puts the answer in its
    /// cache key.
    ///
    /// <para>
    /// The decorator must not re-derive this. A second copy would be free to drift, which is the
    /// same reason the selector <i>calls</i>
    /// <c>AeroApiFlightSearchProvider.HasUsableFlightNumber</c> rather than restating its rule.
    /// </para>
    /// </summary>
    public interface IFlightSearchRouter
    {
        /// <summary>Returns <c>ProviderJobs.AeroDataBox</c> or <c>ProviderJobs.AeroApi</c>.</summary>
        Task<string> ResolveAsync(FlightSearchCriteria criteria, CancellationToken ct);
    }
}
```

- [ ] **Step 2: Write the failing tests**

Append to `FlightAssistant.Infrastructure.Tests/ExternalServices/FlightSearchProviderSelectorTests.cs` a new class in the same file's namespace:

```csharp
    public class FlightSearchRouterTests
    {
        private static FlightSearchRouter Build(string chosen, bool aeroDataBoxConfigured = true)
        {
            var routing = new Mock<IProviderRouting>();
            routing.Setup(r => r.GetProvidersAsync(ProviderJobs.FlightSearchRoute, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new[] { chosen });

            var options = Options.Create(new AeroDataBoxOptions
            {
                ApiKey = aeroDataBoxConfigured ? "key" : null,
            });

            return new FlightSearchRouter(routing.Object, options, NullLogger<FlightSearchRouter>.Instance);
        }

        private static FlightSearchCriteria Route() => new("SOF", "IST", new DateOnly(2026, 9, 20), null);

        [Fact]
        public async Task ResolveAsync_RouteShapeAndSwitchSaysAeroDataBox_ReturnsAeroDataBox()
        {
            var router = Build(ProviderJobs.AeroDataBox);
            Assert.Equal(ProviderJobs.AeroDataBox, await router.ResolveAsync(Route(), default));
        }

        [Fact]
        public async Task ResolveAsync_RouteShapeAndSwitchSaysAeroApi_ReturnsAeroApi()
        {
            var router = Build(ProviderJobs.AeroApi);
            Assert.Equal(ProviderJobs.AeroApi, await router.ResolveAsync(Route(), default));
        }

        [Fact]
        public async Task ResolveAsync_ConfigCeiling_OverridesTheSwitch()
        {
            // AeroDataBox:ApiKey unset is a hard ceiling: a switch must never select a provider the
            // deployment cannot authenticate to.
            var router = Build(ProviderJobs.AeroDataBox, aeroDataBoxConfigured: false);
            Assert.Equal(ProviderJobs.AeroApi, await router.ResolveAsync(Route(), default));
        }

        [Fact]
        public async Task ResolveAsync_FlightNumberShape_StaysOnAeroApiWhateverTheSwitchSays()
        {
            // #422: AeroDataBox resolves a marketing number to its operator (6E4120 -> TK 1028), so
            // this is a correctness rule, not a cost preference, and the switch cannot reach it.
            var router = Build(ProviderJobs.AeroDataBox);
            var criteria = new FlightSearchCriteria("SOF", "IST", new DateOnly(2026, 9, 20), "TK1028");

            Assert.Equal(ProviderJobs.AeroApi, await router.ResolveAsync(criteria, default));
        }

        [Fact]
        public async Task ResolveAsync_SingleAirport_StaysOnAeroApiSoItsOwnGuardStillRefuses()
        {
            var router = Build(ProviderJobs.AeroDataBox);
            var criteria = new FlightSearchCriteria("SOF", null, new DateOnly(2026, 9, 20), null);

            Assert.Equal(ProviderJobs.AeroApi, await router.ResolveAsync(criteria, default));
        }
    }
```

Add the usings the file needs at the top: `FlightAssistant.Application.Interfaces.Providers`, `FlightAssistant.Application.Providers`, `FlightAssistant.Infrastructure.ExternalServices.AeroDataBox`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging.Abstractions`, `Moq`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FlightSearchRouterTests"`
Expected: FAIL to compile — `FlightSearchRouter` does not exist.

- [ ] **Step 4: Implement the router**

```csharp
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Providers;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.ExternalServices
{
    /// <inheritdoc cref="IFlightSearchRouter"/>
    public sealed class FlightSearchRouter : IFlightSearchRouter
    {
        private readonly IProviderRouting _routing;
        private readonly IOptions<AeroDataBoxOptions> _aeroDataBoxOptions;
        private readonly ILogger<FlightSearchRouter> _logger;

        public FlightSearchRouter(
            IProviderRouting routing,
            IOptions<AeroDataBoxOptions> aeroDataBoxOptions,
            ILogger<FlightSearchRouter> logger)
        {
            _routing = routing;
            _aeroDataBoxOptions = aeroDataBoxOptions;
            _logger = logger;
        }

        public async Task<string> ResolveAsync(FlightSearchCriteria criteria, CancellationToken ct)
        {
            // The criteria-shape rule is unchanged from #422 and is NOT switchable. A flight-number
            // search stays on AeroAPI for correctness, and every shape AeroAPI's own search guard
            // rejects still reaches that guard so it keeps producing the same error for the same
            // input it always has.
            if (!IsRouteShaped(criteria))
                return ProviderJobs.AeroApi;

            // Configuration is a hard ceiling, exactly as it is for job switches.
            if (!_aeroDataBoxOptions.Value.IsConfigured)
                return ProviderJobs.AeroApi;

            var providers = await _routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, ct);
            var chosen = providers.Count > 0 ? providers[0] : ProviderJobs.AeroDataBox;

            if (string.Equals(chosen, ProviderJobs.AeroDataBox, StringComparison.Ordinal))
                return ProviderJobs.AeroDataBox;

            if (string.Equals(chosen, ProviderJobs.AeroApi, StringComparison.Ordinal))
                return ProviderJobs.AeroApi;

            // Only reachable if a row was written outside SetProviderRouteCommand, whose validator
            // rejects anything not in ProviderJobs.OptionsFor. Fall back rather than throw: a bad
            // row must not take out every search.
            _logger.LogWarning(
                "Provider route {Job} names unknown provider '{Provider}'; using AeroDataBox.",
                ProviderJobs.FlightSearchRoute, chosen);
            return ProviderJobs.AeroDataBox;
        }

        private static bool IsRouteShaped(FlightSearchCriteria criteria) =>
            !AeroApiFlightSearchProvider.HasUsableFlightNumber(criteria.FlightNumber) &&
            !string.IsNullOrWhiteSpace(criteria.DepartureIata) &&
            !string.IsNullOrWhiteSpace(criteria.ArrivalIata);
    }
}
```

- [ ] **Step 5: Rewrite the selector to read the router**

Replace `FlightSearchProviderSelector`'s body. Keep the class doc, updating the second paragraph to say routing now comes from `IFlightSearchRouter`:

```csharp
    public sealed class FlightSearchProviderSelector : IFlightSearchProvider
    {
        private readonly IFlightSearchProvider _aeroDataBox;
        private readonly IFlightSearchProvider _aeroApi;
        private readonly IFlightSearchRouter _router;
        private readonly ILogger<FlightSearchProviderSelector> _logger;

        public FlightSearchProviderSelector(
            IFlightSearchProvider aeroDataBox,
            IFlightSearchProvider aeroApi,
            IFlightSearchRouter router,
            ILogger<FlightSearchProviderSelector> logger)
        {
            _aeroDataBox = aeroDataBox;
            _aeroApi = aeroApi;
            _router = router;
            _logger = logger;
        }

        public async Task<IReadOnlyList<FlightSearchResult>> SearchAsync(
            FlightSearchCriteria criteria,
            CancellationToken ct = default)
        {
            var provider = await _router.ResolveAsync(criteria, ct);

            if (string.Equals(provider, ProviderJobs.AeroDataBox, StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "Routing flight search {Departure}-{Arrival} on {Date} to AeroDataBox (#422)",
                    criteria.DepartureIata, criteria.ArrivalIata, criteria.Date);
                return await _aeroDataBox.SearchAsync(criteria, ct);
            }

            return await _aeroApi.SearchAsync(criteria, ct);
        }
    }
```

Remove the now-unused `IOptions<AeroDataBoxOptions>` field and the `IsRouteSearchForAeroDataBox` method.

- [ ] **Step 6: Update the DI registration**

In `DependencyInjection.cs`, register the router and defaults before the search registration, and pass the router to the selector:

```csharp
            services.AddSingleton<IProviderRouteDefaults, ProviderRouteDefaults>();
            services.AddSingleton<IProviderRouting, CachedProviderRouting>();
            services.AddScoped<IProviderRouteRepository, ProviderRouteRepository>();
            services.AddScoped<IFlightSearchRouter, FlightSearchRouter>();
```

Then in the `IFlightSearchProvider` registration, replace the selector's last two constructor arguments:

```csharp
                new FlightSearchProviderSelector(
                    sp.GetRequiredService<AeroDataBoxFlightSearchProvider>(),
                    new AeroApiFlightSearchBudgetDecorator(
                        sp.GetRequiredService<AeroApiFlightSearchProvider>(),
                        sp.GetRequiredService<IProviderBudgetGuard>(),
                        sp.GetRequiredService<ILogger<AeroApiFlightSearchBudgetDecorator>>()),
                    sp.GetRequiredService<IFlightSearchRouter>(),
                    sp.GetRequiredService<ILogger<FlightSearchProviderSelector>>()),
```

- [ ] **Step 7: Update the existing selector tests**

`FlightSearchProviderSelectorTests` constructs the selector with `IOptions<AeroDataBoxOptions>`. Replace that argument with a `Mock<IFlightSearchRouter>` whose `ResolveAsync` returns the provider each existing test expects. Do not delete any existing test — each one still asserts a real behaviour, now expressed through the router.

- [ ] **Step 8: Run the full Infrastructure suite**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj`
Expected: PASS. A failure in `AeroApiFlightSearchCachingDecoratorTests` here means Task 5 is needed — that is expected only if you changed the decorator early; otherwise investigate.

- [ ] **Step 9: Commit**

```bash
git add FlightAssistant.Infrastructure/ExternalServices FlightAssistant.Infrastructure/DependencyInjection.cs \
  FlightAssistant.Infrastructure.Tests/ExternalServices
git commit -m "feat(search): extract the routing rule into IFlightSearchRouter (#531)"
```

---

### Task 5: The cache key gains a provider dimension

**Files:**
- Modify: `FlightAssistant.Infrastructure/ExternalServices/AeroApi/Caching/AeroApiFlightSearchCachingDecorator.cs`
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs:332-346` (the comment block and the registration)
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/Caching/AeroApiFlightSearchCachingDecoratorTests.cs`

**Interfaces:**
- Consumes: `IFlightSearchRouter.ResolveAsync` (Task 4).
- Produces: no new public surface. `BuildCacheKey` becomes an instance method taking the resolved provider.

- [ ] **Step 1: Write the failing test**

Add to `AeroApiFlightSearchCachingDecoratorTests`:

```csharp
        [Fact]
        public async Task SearchAsync_SameCriteriaUnderTwoProviders_DoesNotShareACacheEntry()
        {
            // The defect this dimension prevents: before #531 the key was dep|arr|date|fn|cs with no
            // provider in it, so flipping the route switch served the other provider's rows for up to
            // SearchCacheTtlFuture (12 h). That used to need a deploy and a documented prefix flush;
            // a runtime switch would make it a per-button-press problem.
            var criteria = new FlightSearchCriteria("SOF", "IST", new DateOnly(2026, 12, 1), null);

            var aeroDataBoxRows = new List<FlightSearchResult> { Row("FB101") };
            var aeroApiRows = new List<FlightSearchResult> { Row("FB202") };

            var inner = new Mock<IFlightSearchProvider>();
            var router = new Mock<IFlightSearchRouter>();
            var cache = new InMemoryTestCache();

            var decorator = BuildDecorator(inner.Object, router.Object, cache);

            router.Setup(r => r.ResolveAsync(criteria, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(ProviderJobs.AeroDataBox);
            inner.Setup(p => p.SearchAsync(criteria, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(aeroDataBoxRows);
            var first = await decorator.SearchAsync(criteria, default);

            // The switch is flipped. The same criteria must now reach the provider, not the cache.
            router.Setup(r => r.ResolveAsync(criteria, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(ProviderJobs.AeroApi);
            inner.Setup(p => p.SearchAsync(criteria, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(aeroApiRows);
            var second = await decorator.SearchAsync(criteria, default);

            Assert.Equal("FB101", first[0].FlightNumber);
            Assert.Equal("FB202", second[0].FlightNumber);
            inner.Verify(p => p.SearchAsync(criteria, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Fact]
        public async Task SearchAsync_SameCriteriaSameProvider_StillHitsTheCache()
        {
            var criteria = new FlightSearchCriteria("SOF", "IST", new DateOnly(2026, 12, 1), null);

            var inner = new Mock<IFlightSearchProvider>();
            var router = new Mock<IFlightSearchRouter>();
            var cache = new InMemoryTestCache();
            var decorator = BuildDecorator(inner.Object, router.Object, cache);

            router.Setup(r => r.ResolveAsync(criteria, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(ProviderJobs.AeroDataBox);
            inner.Setup(p => p.SearchAsync(criteria, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new List<FlightSearchResult> { Row("FB101") });

            await decorator.SearchAsync(criteria, default);
            await decorator.SearchAsync(criteria, default);

            inner.Verify(p => p.SearchAsync(criteria, It.IsAny<CancellationToken>()), Times.Once);
        }
```

Reuse the file's existing helpers for building the decorator, its fake cache and a `FlightSearchResult` row. If the existing file has no `BuildDecorator`/`Row`/`InMemoryTestCache`, add them, naming them exactly as above so both new tests compile, and construct results with whatever `FlightSearchResult`'s real positional parameters are — read the record before writing `Row`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiFlightSearchCachingDecoratorTests"`
Expected: FAIL — the decorator's constructor takes no router, so this does not compile.

- [ ] **Step 3: Add the provider to the key**

In `AeroApiFlightSearchCachingDecorator`, add the router as a constructor dependency after `inner`:

```csharp
        private readonly IFlightSearchRouter _router;
```

set it in the constructor, and change `SearchAsync`'s first two lines:

```csharp
            // Which provider will serve this search is part of the cache identity. The router is the
            // single owner of that rule (#531) — re-deriving it here would be a second copy free to
            // drift from the selector's.
            var provider = await _router.ResolveAsync(criteria, ct);
            var key = BuildCacheKey(criteria, provider);
```

and make the key builder take it:

```csharp
        private static string BuildCacheKey(FlightSearchCriteria criteria, string provider) =>
            $"{CacheKeyPrefix}provider={provider}" +
            $"|dep={Normalize(criteria.DepartureIata)}" +
            $"|arr={Normalize(criteria.ArrivalIata)}" +
            $"|date={criteria.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
            $"|fn={Normalize(criteria.FlightNumber)}" +
            $"|cs={criteria.IncludeCodeshares}";
```

Add a class-doc paragraph:

```csharp
    /// <para>
    /// <b>The key carries the provider (#531).</b> Before this, the key was
    /// <c>dep|arr|date|fn|cs</c> with no provider dimension, so a route-search key could be served
    /// the other provider's rows for up to <see cref="AeroApiOptions.SearchCacheTtlFuture"/> after
    /// routing changed — tolerable when that took a deploy, not when an operator can flip it from
    /// the admin panel. The provider comes from <see cref="IFlightSearchRouter"/>, the same single
    /// owner the selector reads, and this retires the "flush the <c>aeroapi:flight-search:</c>
    /// prefix on rollback" step that used to be required. The cost is one extra cache miss per key
    /// per provider after a flip.
    /// </para>
```

- [ ] **Step 4: Update the DI registration and its comment**

Pass the router into the decorator:

```csharp
            services.AddScoped<IFlightSearchProvider>(sp => new AeroApiFlightSearchCachingDecorator(
                new FlightSearchProviderSelector(/* as in Task 4 */),
                sp.GetRequiredService<IFlightSearchRouter>(),
                sp.GetRequiredService<ICacheService>(),
                sp.GetRequiredService<IOptions<AeroApiOptions>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<AeroApiFlightSearchCachingDecorator>>()));
```

Replace the stale paragraph of the comment above it (the one beginning "The cache key (AeroApiFlightSearchCachingDecorator.BuildCacheKey) has no provider dimension") with:

```csharp
            // The cache key carries the resolved provider as of #531, so the two providers can no
            // longer serve each other's cached rows and the prefix-flush-on-rollback step this
            // comment used to require is gone. Routing is now a persisted admin decision
            // (provider_routes) on top of the AeroDataBox:ApiKey ceiling, read through
            // IFlightSearchRouter by both the selector and the cache.
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiFlightSearchCachingDecoratorTests"`
Expected: PASS, including every pre-existing test in that class.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add FlightAssistant.Infrastructure FlightAssistant.Infrastructure.Tests
git commit -m "fix(search): put the resolved provider in the search cache key (#531)"
```

---

### Task 6: `GET`/`PUT /admin/providers`

**Files:**
- Create: `FlightAssistant.Contracts/Requests/Admin/SetProviderRouteRequest.cs`
- Create: `FlightAssistant.Contracts/Responses/Admin/AdminProviderRouteDto.cs`
- Create: `FlightAssistant.Application/Queries/Admin/Providers/GetProviderRoutesQuery.cs`
- Create: `FlightAssistant.Application/Commands/Admin/Providers/SetProviderRouteCommand.cs`
- Create: `FlightAssistant.API/Endpoints/Admin/AdminProviderEndpoints.cs`
- Modify: `FlightAssistant.API/Program.cs` (map the group beside `MapAdminJobEndpoints`)
- Test: `FlightAssistant.API.Tests/Integration/AdminProviderEndpointTests.cs`
- Test: `FlightAssistant.Application.Tests/Commands/Admin/Providers/SetProviderRouteCommandTests.cs`

**Interfaces:**
- Consumes: `IProviderRouting`, `IProviderRouteRepository`, `ProviderJobs`.
- Produces: `AdminProviderRouteDto(string Job, string DisplayName, IReadOnlyList<string> Providers, IReadOnlyList<string> Options, bool IsDefault, bool CanChange, string? FixedReason, DateTime? ChangedUtc, string? ChangedBy, string? Reason)`; `SetProviderRouteRequest(IReadOnlyList<string> Providers, string? Reason = null)`; `GetProviderRoutesQuery`; `SetProviderRouteCommand(string Job, IReadOnlyList<string> Providers, string? Reason)`.

- [ ] **Step 1: Create the contracts**

```csharp
namespace FlightAssistant.Contracts.Requests.Admin
{
    /// <summary>Body of <c>PUT /admin/providers/{job}</c>.</summary>
    /// <param name="Providers">The chosen providers, in the order they should be asked. One name for
    /// <c>flight-search-route</c>; a verification key may name several.</param>
    /// <param name="Reason">Optional operator note. Stored on the row and folded into the audit
    /// row's summary, so a later reader can see why, not just what.</param>
    public sealed record SetProviderRouteRequest(IReadOnlyList<string> Providers, string? Reason = null);
}
```

```csharp
namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <param name="IsDefault">True when no row exists and these providers come from configuration.</param>
    /// <param name="CanChange">False when configuration bars every alternative — for
    /// <c>flight-search-route</c>, when <c>AeroDataBox:ApiKey</c> is unset. The panel disables the
    /// control and shows why rather than hiding it, matching <c>AdminJobDto.CanToggle</c>.</param>
    /// <param name="FixedReason">Why this decision cannot be changed, or null when it can. Non-null
    /// only on the read-only map rows added in PR 3.</param>
    public sealed record AdminProviderRouteDto(
        string Job,
        string DisplayName,
        IReadOnlyList<string> Providers,
        IReadOnlyList<string> Options,
        bool IsDefault,
        bool CanChange,
        string? FixedReason,
        DateTime? ChangedUtc,
        string? ChangedBy,
        string? Reason);
}
```

- [ ] **Step 2: Write the failing handler tests**

Create `FlightAssistant.Application.Tests/Commands/Admin/Providers/SetProviderRouteCommandTests.cs`:

```csharp
using FlightAssistant.Application.Commands.Admin.Providers;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Providers;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Providers.Entities;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace FlightAssistant.Application.Tests.Commands.Admin.Providers
{
    public class SetProviderRouteCommandTests
    {
        private readonly Mock<IProviderRouteRepository> _repository = new();
        private readonly Mock<IAdminAuditRepository> _audit = new();
        private readonly Mock<IProviderRouteDefaults> _defaults = new();
        private readonly Mock<IAdminContext> _adminContext = new();
        private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

        private SetProviderRouteCommandHandler Build()
        {
            _adminContext.SetupGet(c => c.ActorId).Returns("oid-1");
            _adminContext.SetupGet(c => c.ActorName).Returns("Ada");
            _adminContext.SetupGet(c => c.CorrelationId).Returns("corr-1");
            _defaults.Setup(d => d.For(ProviderJobs.FlightSearchRoute))
                     .Returns(new[] { ProviderJobs.AeroDataBox });
            return new SetProviderRouteCommandHandler(
                _repository.Object, _defaults.Object, _adminContext.Object, _audit.Object, _time);
        }

        [Fact]
        public async Task Handle_UnknownJob_Throws404()
        {
            var handler = Build();
            var ex = await Assert.ThrowsAsync<DomainException>(() =>
                handler.Handle(new SetProviderRouteCommand("nope", new[] { "aeroapi" }, null), default));
            Assert.Equal(404, ex.StatusCode);
        }

        [Fact]
        public async Task Handle_ProviderNotAnOptionForTheJob_Throws400()
        {
            var handler = Build();
            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.FlightSearchRoute, new[] { "opensky" }, null), default));
            Assert.Equal(400, ex.StatusCode);
            Assert.Contains("opensky", ex.Message);
        }

        [Fact]
        public async Task Handle_ConfigBarsEveryAlternative_Throws409AndWritesNothing()
        {
            // AeroDataBox:ApiKey unset leaves AeroApi as the only reachable provider, so the row
            // would be a decision the deployment cannot honour. 409, not 403: the caller is
            // permitted, the environment is what refuses. Nothing is written, so a stored row always
            // reflects a real operator decision rather than a rejected attempt.
            _defaults.Setup(d => d.For(ProviderJobs.FlightSearchRoute)).Returns(new[] { ProviderJobs.AeroApi });
            _defaults.Setup(d => d.IsCeilinged(ProviderJobs.FlightSearchRoute)).Returns(true);

            var handler = Build();
            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.FlightSearchRoute, new[] { ProviderJobs.AeroDataBox }, null),
                default));

            Assert.Equal(409, ex.StatusCode);
            _repository.Verify(r => r.UpsertAsync(It.IsAny<ProviderRoute>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Handle_Valid_UpsertsTheRowAndWritesOneAuditRowCarryingTheReason()
        {
            var handler = Build();

            var result = await handler.Handle(new SetProviderRouteCommand(
                ProviderJobs.FlightSearchRoute, new[] { ProviderJobs.AeroApi }, "quota gone"), default);

            _repository.Verify(r => r.UpsertAsync(
                It.Is<ProviderRoute>(row =>
                    row.Job == ProviderJobs.FlightSearchRoute &&
                    row.Providers == ProviderJobs.AeroApi &&
                    row.ChangedBy == "Ada" &&
                    row.Reason == "quota gone"),
                It.IsAny<CancellationToken>()), Times.Once);

            _audit.Verify(a => a.AddAsync(
                It.Is<AdminAuditLog>(log =>
                    log.EntityType == "ProviderRoute" &&
                    log.EntityId == ProviderJobs.FlightSearchRoute &&
                    log.Summary!.Contains("quota gone")),
                It.IsAny<CancellationToken>()), Times.Once);
            _audit.Verify(a => a.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

            Assert.Equal(new[] { ProviderJobs.AeroApi }, result.Providers);
            Assert.False(result.IsDefault);
        }
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SetProviderRouteCommandTests"`
Expected: FAIL to compile — the command does not exist and `IProviderRouteDefaults` has no `IsCeilinged`.

- [ ] **Step 4: Add `IsCeilinged` to `IProviderRouteDefaults`**

```csharp
        /// <summary>
        /// True when configuration leaves this key no real choice — for
        /// <c>flight-search-route</c>, when <c>AeroDataBox:ApiKey</c> is unset. A write against a
        /// ceilinged key is refused, matching how <c>ToggleJobCommand</c> refuses a job whose config
        /// has it off.
        /// </summary>
        bool IsCeilinged(string job);
```

And in `ProviderRouteDefaults`:

```csharp
        public bool IsCeilinged(string job) => job switch
        {
            ProviderJobs.FlightSearchRoute => !_aeroDataBox.Value.IsConfigured,
            _ => throw new InvalidOperationException($"'{job}' is not a known provider routing key.")
        };
```

- [ ] **Step 5: Implement the command**

```csharp
using System.Diagnostics;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Providers;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Providers.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Providers
{
    /// <summary>
    /// Changes one routing decision (#531) — what an operator presses on the panel.
    ///
    /// <para>
    /// Configuration is a hard ceiling, the same shape <c>ToggleJobCommand</c> uses: a PUT against a
    /// key whose alternatives configuration bars refuses with 409 — the caller is permitted, the
    /// environment is what refuses — and writes nothing at all, so a stored row always reflects a
    /// real operator decision rather than a rejected attempt.
    /// </para>
    /// </summary>
    public sealed record SetProviderRouteCommand(
        string Job, IReadOnlyList<string> Providers, string? Reason) : IRequest<AdminProviderRouteDto>;

    public sealed class SetProviderRouteCommandHandler
        : IRequestHandler<SetProviderRouteCommand, AdminProviderRouteDto>
    {
        private readonly IProviderRouteRepository _repository;
        private readonly IProviderRouteDefaults _defaults;
        private readonly IAdminContext _adminContext;
        private readonly IAdminAuditRepository _auditRepository;
        private readonly TimeProvider _timeProvider;

        public SetProviderRouteCommandHandler(
            IProviderRouteRepository repository,
            IProviderRouteDefaults defaults,
            IAdminContext adminContext,
            IAdminAuditRepository auditRepository,
            TimeProvider timeProvider)
        {
            _repository = repository;
            _defaults = defaults;
            _adminContext = adminContext;
            _auditRepository = auditRepository;
            _timeProvider = timeProvider;
        }

        public async Task<AdminProviderRouteDto> Handle(SetProviderRouteCommand request, CancellationToken ct)
        {
            if (!ProviderJobs.All.Contains(request.Job, StringComparer.Ordinal))
                throw new DomainException($"There is no provider routing key '{request.Job}'.", 404);

            if (request.Providers is null || request.Providers.Count == 0)
                throw new DomainException("At least one provider must be named.", 400);

            var options = ProviderJobs.OptionsFor(request.Job);
            foreach (var provider in request.Providers)
            {
                if (!options.Contains(provider, StringComparer.Ordinal))
                {
                    throw new DomainException(
                        $"'{provider}' is not a provider for '{request.Job}'. Options: {string.Join(", ", options)}.",
                        400);
                }
            }

            if (_defaults.IsCeilinged(request.Job))
            {
                throw new DomainException(
                    $"'{request.Job}' cannot be changed: this environment configures no alternative provider for it.",
                    409);
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var changedBy = _adminContext.ActorName ?? _adminContext.ActorId;
            var joined = string.Join(',', request.Providers);

            await _repository.UpsertAsync(new ProviderRoute
            {
                Job = request.Job,
                Providers = joined,
                ChangedUtc = now,
                ChangedBy = changedBy,
                Reason = request.Reason,
            }, ct);

            await WriteAuditAsync(request, joined, now, ct);

            return new AdminProviderRouteDto(
                Job: request.Job,
                DisplayName: ProviderJobs.DisplayNameFor(request.Job),
                Providers: request.Providers,
                Options: options,
                IsDefault: false,
                CanChange: true,
                FixedReason: null,
                ChangedUtc: now,
                ChangedBy: changedBy,
                Reason: request.Reason);
        }

        /// <summary>
        /// Writes the one audit row this change produces. <c>AdminAuditInterceptor</c> excludes
        /// <see cref="ProviderRoute"/> precisely so this write can carry the operator's reason text,
        /// which its generic before/after diff has no field for — letting both fire would put two
        /// rows in the log for one change, saying different things.
        /// </summary>
        private async Task WriteAuditAsync(
            SetProviderRouteCommand request, string joined, DateTime now, CancellationToken ct)
        {
            var actorId = _adminContext.ActorId;
            if (string.IsNullOrWhiteSpace(actorId)) return;

            var summary = string.IsNullOrWhiteSpace(request.Reason)
                ? $"Routed {request.Job} to {joined}"
                : $"Routed {request.Job} to {joined}: {request.Reason}";

            var audit = AdminAuditLog.Create(
                actorId: actorId,
                actorName: _adminContext.ActorName,
                action: AdminAuditAction.Update,
                entityType: "ProviderRoute",
                entityId: request.Job,
                summary: summary,
                traceId: _adminContext.CorrelationId ?? Activity.Current?.Id,
                actionUtc: now);

            await _auditRepository.AddAsync(audit, ct);
            await _auditRepository.SaveChangesAsync(ct);
        }
    }
}
```

- [ ] **Step 6: Implement the query**

```csharp
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Providers;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Providers
{
    /// <summary>Every routing decision, its current value, and whether it can be changed. Backs
    /// <c>GET /admin/providers</c>. PR 3 widens this to the full operation map.</summary>
    public sealed record GetProviderRoutesQuery : IRequest<IReadOnlyList<AdminProviderRouteDto>>;

    public sealed class GetProviderRoutesQueryHandler
        : IRequestHandler<GetProviderRoutesQuery, IReadOnlyList<AdminProviderRouteDto>>
    {
        private readonly IProviderRouting _routing;
        private readonly IProviderRouteDefaults _defaults;

        public GetProviderRoutesQueryHandler(IProviderRouting routing, IProviderRouteDefaults defaults)
        {
            _routing = routing;
            _defaults = defaults;
        }

        public async Task<IReadOnlyList<AdminProviderRouteDto>> Handle(
            GetProviderRoutesQuery request, CancellationToken ct)
        {
            var rows = new List<AdminProviderRouteDto>(ProviderJobs.All.Count);

            foreach (var job in ProviderJobs.All)
            {
                var state = await _routing.GetAsync(job, ct);
                rows.Add(new AdminProviderRouteDto(
                    Job: job,
                    DisplayName: ProviderJobs.DisplayNameFor(job),
                    Providers: state.Providers,
                    Options: ProviderJobs.OptionsFor(job),
                    IsDefault: state.IsDefault,
                    CanChange: !_defaults.IsCeilinged(job),
                    FixedReason: null,
                    ChangedUtc: state.ChangedUtc,
                    ChangedBy: state.ChangedBy,
                    Reason: state.Reason));
            }

            return rows;
        }
    }
}
```

- [ ] **Step 7: Map the endpoints**

```csharp
using FlightAssistant.Application.Commands.Admin.Providers;
using FlightAssistant.Application.Queries.Admin.Providers;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    /// <summary>
    /// <c>GET</c>/<c>PUT</c> for provider routing (#531). Mapped onto the shared <c>/admin</c> group,
    /// which already carries <c>RequireAuthorization("AdminOnly")</c> and
    /// <c>RequireCors("AdminPanel")</c> — neither is re-applied here.
    /// </summary>
    public static class AdminProviderEndpoints
    {
        public static void MapAdminProviderEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("providers").WithTags("Admin - Providers");

            // GET /admin/providers
            group.MapGet("", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetProviderRoutesQuery(), ct)))
            .WithName("AdminGetProviderRoutes")
            .WithSummary("Which provider serves each routing decision")
            .WithDescription(
                "Every routing decision an admin can change, its current providers, and whether the " +
                "value came from an operator or from configuration. 'canChange' is false when this " +
                "environment configures no alternative — for route search, when AeroDataBox:ApiKey is " +
                "unset — and configuration is a hard ceiling no change can lift.")
            .Produces<List<AdminProviderRouteDto>>(200);

            // PUT /admin/providers/{job}
            group.MapPut("{job}", async (
                string job,
                SetProviderRouteRequest request,
                IMediator mediator,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(
                    new SetProviderRouteCommand(job, request.Providers, request.Reason), ct);
                return Results.Ok(result);
            })
            .WithName("AdminSetProviderRoute")
            .WithSummary("Choose which provider serves a routing decision")
            .WithDescription(
                "Takes effect within the 10-second read cache for searches not already cached. " +
                "Cached search results are unaffected and are not wrong: the search cache key carries " +
                "the provider, so the two providers never serve each other's rows. A verification key " +
                "affects newly queued requests and track-time provisioning only -- the job uses each " +
                "request's own stored sources, so requests already in the queue keep the sources they " +
                "were queued with. Audited. 400 when a provider is not an option for this key, 404 " +
                "when the key is unknown, 409 when this environment configures no alternative.")
            .Produces<AdminProviderRouteDto>(200)
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
        }
    }
}
```

In `Program.cs`, call `admin.MapAdminProviderEndpoints();` directly after `admin.MapAdminJobEndpoints();`.

- [ ] **Step 8: Write the failing endpoint tests**

Create `FlightAssistant.API.Tests/Integration/AdminProviderEndpointTests.cs`, following `AdminJobEndpointTests`' wiring exactly (read it first for the factory, the admin auth header helper, and the JSON options):

```csharp
        [Fact]
        public async Task GetProviders_ReturnsTheRouteSearchDecision()
        {
            var client = CreateAdminClient();

            var rows = await client.GetFromJsonAsync<List<AdminProviderRouteDto>>("/admin/providers");

            Assert.NotNull(rows);
            var route = Assert.Single(rows!, r => r.Job == "flight-search-route");
            Assert.Equal("Route search", route.DisplayName);
            Assert.Contains("aeroapi", route.Options);
            Assert.Contains("aerodatabox", route.Options);
        }

        [Fact]
        public async Task PutProvider_UnknownKey_Returns404()
        {
            var client = CreateAdminClient();

            var response = await client.PutAsJsonAsync("/admin/providers/nope",
                new SetProviderRouteRequest(new[] { "aeroapi" }));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task PutProvider_ProviderThatIsNotAnOption_Returns400()
        {
            var client = CreateAdminClient();

            var response = await client.PutAsJsonAsync("/admin/providers/flight-search-route",
                new SetProviderRouteRequest(new[] { "opensky" }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
```

Note: the API suite runs on the InMemory provider with the audit interceptor stripped (#319), so do **not** assert an audit row here. These tests prove the route is wired and the status codes are right.

There is deliberately **no** PostgreSQL-backed audit test for this write, unlike the device-token delete. That one needs one because its audit row comes from `AdminAuditInterceptor`, which the API factory strips — so only a real interceptor proves it. This row is written **explicitly** by the handler through `IAdminAuditRepository`, so Step 2's mock verification proves the thing that can actually break: that the handler writes exactly one row carrying the reason. If the write is ever switched to rely on the interceptor instead, that reasoning inverts and a PostgreSQL test becomes mandatory.

- [ ] **Step 9: Run both test projects**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SetProviderRouteCommandTests"`
Expected: PASS, 4 tests.

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~AdminProviderEndpointTests"`
Expected: PASS, 3 tests.

- [ ] **Step 10: Regenerate the OpenAPI spec**

Run from the repo root: `scripts/generate-openapi.sh`
Expected: `spec/openapi.json` gains the two operations. Confirm with `git diff --stat spec/openapi.json`.

- [ ] **Step 11: Run everything**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 12: Commit and open the PR**

```bash
git add FlightAssistant.Contracts FlightAssistant.Application FlightAssistant.API \
  FlightAssistant.Application.Tests FlightAssistant.API.Tests spec/openapi.json
git commit -m "feat(admin): GET/PUT /admin/providers for route search (#531)"
git push -u origin feature/531-provider-routing-switch
gh pr create --base development --title "Runtime provider routing switch: mechanism, route search, cache key (#531)" \
  --body "Closes the first of #531's three parts. Adds provider_routes (a sibling of job_switches, not a widening of it), a 10s-cached read with a configured-default fallback, an explicitly-written audit row carrying the operator reason, and GET/PUT /admin/providers for route search.

The routing rule moves into IFlightSearchRouter so the search cache key can carry the resolved provider. That retires the flush-the-aeroapi:flight-search:-prefix-on-rollback step recorded in DependencyInjection.cs: the two providers can no longer serve each other's cached rows.

Spec: docs/superpowers/specs/2026-09-19-provider-routing-switch-design.md
Plan: docs/superpowers/plans/2026-09-19-provider-routing-switch.md"
```

---

# PR 2 — verification defaults

Create a GitHub issue first ("Make the verification default sources runtime-switchable (#531 PR 2)"), then `git checkout development && git pull && git checkout -b feature/{ISSUE}-verification-source-switch`.

### Task 7: Switch-aware verification defaults

**Files:**
- Create: `FlightAssistant.Application/Interfaces/Verification/IVerificationSourceDefaults.cs`
- Create: `FlightAssistant.Application/Verification/VerificationSourceDefaults.cs`
- Modify: `FlightAssistant.Application/Providers/ProviderJobs.cs`
- Modify: `FlightAssistant.Infrastructure/Providers/ProviderRouteDefaults.cs`
- Test: `FlightAssistant.Application.Tests/Verification/VerificationSourceDefaultsTests.cs`

**Interfaces:**
- Consumes: `IProviderRouting.GetProvidersAsync`, `IVerificationJobOptions.DefaultSourcesFor`.
- Produces: `IVerificationSourceDefaults.ForAsync(VerificationEntityType entityType, CancellationToken ct) → Task<IReadOnlyList<string>>`. `ProviderJobs.VerificationAirport/Airline/Country` = `"verification-airport"` / `"verification-airline"` / `"verification-country"`. `ProviderJobs.ForEntityType(VerificationEntityType)` → the key name.

- [ ] **Step 1: Extend `ProviderJobs`**

```csharp
        public const string VerificationAirport = "verification-airport";
        public const string VerificationAirline = "verification-airline";
        public const string VerificationCountry = "verification-country";

        public static readonly IReadOnlyList<string> All =
            [FlightSearchRoute, VerificationAirport, VerificationAirline, VerificationCountry];

        /// <summary>The routing key that decides which sources check one entity type.</summary>
        public static string ForEntityType(VerificationEntityType entityType) => entityType switch
        {
            VerificationEntityType.Airport => VerificationAirport,
            VerificationEntityType.Airline => VerificationAirline,
            VerificationEntityType.Country => VerificationCountry,
            _ => throw new ArgumentOutOfRangeException(
                nameof(entityType), entityType, "Unknown verification entity type.")
        };
```

Extend `DisplayNameFor` with `"Verification — airports"`, `"Verification — airlines"`, `"Verification — countries"`.

`OptionsFor` cannot answer for a verification key from a static list — which sources are registered and which entity types they support is a runtime fact. Return an empty list for those three and add the comment:

```csharp
            // Verification keys have no static option list: the real answer is every registered
            // source whose SupportedEntityTypes includes this key's entity type, which only
            // IVerificationSourceRegistry knows. GetProviderRoutesQueryHandler and the command
            // validator both ask the registry instead.
            VerificationAirport or VerificationAirline or VerificationCountry => [],
```

- [ ] **Step 2: Extend `ProviderRouteDefaults`**

Take `IVerificationJobOptions` as a second dependency and return its configured list:

```csharp
            ProviderJobs.VerificationAirport =>
                _verificationOptions.DefaultSourcesFor(VerificationEntityType.Airport),
            ProviderJobs.VerificationAirline =>
                _verificationOptions.DefaultSourcesFor(VerificationEntityType.Airline),
            ProviderJobs.VerificationCountry =>
                _verificationOptions.DefaultSourcesFor(VerificationEntityType.Country),
```

and for `IsCeilinged`, verification keys are never ceilinged — configuration always names at least one source and the registry decides validity:

```csharp
            ProviderJobs.VerificationAirport or ProviderJobs.VerificationAirline
                or ProviderJobs.VerificationCountry => false,
```

- [ ] **Step 3: Write the failing tests**

Create `FlightAssistant.Application.Tests/Verification/VerificationSourceDefaultsTests.cs`:

```csharp
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Providers;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Enums;
using Moq;

namespace FlightAssistant.Application.Tests.Verification
{
    public class VerificationSourceDefaultsTests
    {
        private readonly Mock<IProviderRouting> _routing = new();
        private readonly Mock<IVerificationJobOptions> _options = new();

        private VerificationSourceDefaults Build() => new(_routing.Object, _options.Object);

        [Fact]
        public async Task ForAsync_UsesTheSwitchValue()
        {
            _routing.Setup(r => r.GetProvidersAsync(ProviderJobs.VerificationAirline, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new[] { "ourairports" });

            var sources = await Build().ForAsync(VerificationEntityType.Airline, default);

            Assert.Equal(new[] { "ourairports" }, sources);
        }

        [Fact]
        public async Task ForAsync_EmptySwitchValue_FallsBackToConfiguration()
        {
            // GetProvidersAsync already falls back to the configured default itself; this guards the
            // remaining case where a row exists but parses to nothing, so the caller never gets an
            // empty source list and silently queues a request that can never be answered.
            _routing.Setup(r => r.GetProvidersAsync(ProviderJobs.VerificationCountry, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Array.Empty<string>());
            _options.Setup(o => o.DefaultSourcesFor(VerificationEntityType.Country))
                    .Returns(new[] { "mledoze" });

            var sources = await Build().ForAsync(VerificationEntityType.Country, default);

            Assert.Equal(new[] { "mledoze" }, sources);
        }

        [Fact]
        public async Task ForAsync_PreservesAskOrder()
        {
            _routing.Setup(r => r.GetProvidersAsync(ProviderJobs.VerificationAirport, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new[] { "ourairports", "aeroapi" });

            var sources = await Build().ForAsync(VerificationEntityType.Airport, default);

            Assert.Equal(new[] { "ourairports", "aeroapi" }, sources);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~VerificationSourceDefaultsTests"`
Expected: FAIL to compile — `VerificationSourceDefaults` does not exist.

- [ ] **Step 5: Implement it**

```csharp
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Providers;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// Which sources check one entity type, honouring the runtime switch (#531) and falling back to
    /// <c>Verification:DefaultSources:*</c>.
    ///
    /// <para>
    /// <b>This does not reach the verification job.</b> The job uses each request's own stored
    /// <c>sources</c>, so changing a switch affects newly <i>queued</i> requests and the track-time
    /// provisioning path, and leaves everything already in the queue alone.
    /// </para>
    /// </summary>
    public sealed class VerificationSourceDefaults : IVerificationSourceDefaults
    {
        private readonly IProviderRouting _routing;
        private readonly IVerificationJobOptions _options;

        public VerificationSourceDefaults(IProviderRouting routing, IVerificationJobOptions options)
        {
            _routing = routing;
            _options = options;
        }

        public async Task<IReadOnlyList<string>> ForAsync(VerificationEntityType entityType, CancellationToken ct)
        {
            var chosen = await _routing.GetProvidersAsync(ProviderJobs.ForEntityType(entityType), ct);
            return chosen.Count > 0 ? chosen : _options.DefaultSourcesFor(entityType);
        }
    }
}
```

with the interface:

```csharp
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Interfaces.Verification
{
    /// <summary>
    /// The sources to ask for one entity type, as an admin has chosen them or as configuration
    /// defaults them. Replaces direct <c>IVerificationJobOptions.DefaultSourcesFor</c> calls at the
    /// two sites that queue or provision; that method stays as the fallback.
    /// </summary>
    public interface IVerificationSourceDefaults
    {
        Task<IReadOnlyList<string>> ForAsync(VerificationEntityType entityType, CancellationToken ct);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~VerificationSourceDefaultsTests"`
Expected: PASS, 3 tests.

- [ ] **Step 7: Commit**

```bash
git add FlightAssistant.Application FlightAssistant.Infrastructure/Providers FlightAssistant.Application.Tests
git commit -m "feat(verification): switch-aware default sources (#531)"
```

---

### Task 8: Wire both call sites

**Files:**
- Modify: `FlightAssistant.Application/EventHandlers/FlightTrackedVerificationEventHandler.cs:149`
- Modify: `FlightAssistant.Application/ReferenceData/ReferenceDataProvisioner.cs:731`
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs` (register `IVerificationSourceDefaults`)
- Test: existing `FlightTrackedVerificationEventHandlerTests`, `ReferenceDataProvisionerTests` — extend

**Interfaces:**
- Consumes: `IVerificationSourceDefaults.ForAsync` (Task 7).
- Produces: nothing new.

- [ ] **Step 1: Write the failing test for the event handler**

Add to `FlightTrackedVerificationEventHandlerTests`:

```csharp
        [Fact]
        public async Task Handle_QueuesWithTheSwitchedSources_NotTheConfiguredOnes()
        {
            // The whole point of the switch: an admin routes airline checks to a free source and the
            // next track request queues against that source, with no restart.
            _sourceDefaults.Setup(d => d.ForAsync(VerificationEntityType.Airline, It.IsAny<CancellationToken>()))
                           .ReturnsAsync(new[] { "ourairports" });

            await Handler().Handle(new FlightTracked(FlightId, TravelerId, DateTime.UtcNow), default);

            _queue.Verify(q => q.EnqueueAsync(
                VerificationEntityType.Airline,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.Is<IReadOnlyList<string>>(s => s.SequenceEqual(new[] { "ourairports" })),
                VerificationTrigger.Flight,
                It.IsAny<string>(),
                It.IsAny<decimal>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
```

Match the real `FlightTracked` constructor and the real `EnqueueAsync` signature — read both before writing this, and add the `_sourceDefaults` mock to the fixture the other tests in the class already use.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~FlightTrackedVerificationEventHandlerTests"`
Expected: FAIL to compile — the handler takes no `IVerificationSourceDefaults`.

- [ ] **Step 3: Change the event handler**

Replace `IVerificationJobOptions _options`'s use in `EnqueueAsync` only. The handler still needs `_options` for `StaleAfterDays`, so keep that field and add the new dependency:

```csharp
        private async Task EnqueueAsync(
            VerificationEntityType entityType, IReadOnlyCollection<string> keys, CancellationToken ct)
        {
            // #531: the runtime switch, falling back to Verification:DefaultSources:*.
            var sources = await _sourceDefaults.ForAsync(entityType, ct);

            var cost = 0m;
            foreach (var name in sources)
                cost += _registry.TryGet(name)?.CostPerRowUsd ?? 0m;

            await _queue.EnqueueAsync(
                entityType, keys, sources, VerificationTrigger.Flight, VerificationJobActor.ActorId, cost, ct);
        }
```

Update its three call sites in the same class to `await` it.

- [ ] **Step 4: Change the provisioner**

In `ReferenceDataProvisioner.FetchFromSourcesAsync`, replace the first line:

```csharp
            // #531: the runtime switch, falling back to Verification:DefaultSources:*.
            var sourceNames = await _sourceDefaults.ForAsync(type, ct).ConfigureAwait(false);
```

The method is already `async Task<Fetch>`, so no signature change. Add the constructor dependency and keep `_verificationOptions` if anything else in the class reads it; remove the field if this was its only use.

- [ ] **Step 5: Register it**

In `DependencyInjection.cs`:

```csharp
            services.AddScoped<IVerificationSourceDefaults, VerificationSourceDefaults>();
```

- [ ] **Step 6: Run the affected suites**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj`
Expected: PASS. Existing tests that constructed either class will need the new mock added — that is expected, and each must keep asserting what it asserted before.

- [ ] **Step 7: Run everything**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add FlightAssistant.Application FlightAssistant.Infrastructure/DependencyInjection.cs FlightAssistant.Application.Tests
git commit -m "feat(verification): read default sources through the switch at both call sites (#531)"
```

---

### Task 9: Registry validation on write, and the three keys on the endpoint

**Files:**
- Modify: `FlightAssistant.Application/Commands/Admin/Providers/SetProviderRouteCommand.cs`
- Modify: `FlightAssistant.Application/Queries/Admin/Providers/GetProviderRoutesQuery.cs`
- Test: `FlightAssistant.Application.Tests/Commands/Admin/Providers/SetProviderRouteCommandTests.cs`

**Interfaces:**
- Consumes: `IVerificationSourceRegistry.TryGet`, `IVerificationSource.SupportedEntityTypes`.
- Produces: no new surface.

- [ ] **Step 1: Write the failing tests**

Add to `SetProviderRouteCommandTests`:

```csharp
        [Fact]
        public async Task Handle_VerificationKeyNamingAnUnregisteredSource_Throws400()
        {
            // Configuration cannot refuse this: VerificationDefaultSources.Validate checks only the
            // join grammar, which is why a pre-#401 'aeroapi' default would have completed every
            // request straight to Error. A write is a request/response, so it can refuse.
            _registry.Setup(r => r.TryGet("nosuch")).Returns((IVerificationSource?)null);

            var handler = Build();
            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.VerificationAirport, new[] { "nosuch" }, null), default));

            Assert.Equal(400, ex.StatusCode);
            Assert.Contains("nosuch", ex.Message);
        }

        [Fact]
        public async Task Handle_VerificationKeyNamingASourceThatDoesNotSupportTheEntityType_Throws400()
        {
            var countryOnly = new Mock<IVerificationSource>();
            countryOnly.SetupGet(s => s.Name).Returns("mledoze");
            countryOnly.SetupGet(s => s.SupportedEntityTypes)
                       .Returns(new[] { VerificationEntityType.Country });
            _registry.Setup(r => r.TryGet("mledoze")).Returns(countryOnly.Object);

            var handler = Build();
            var ex = await Assert.ThrowsAsync<DomainException>(() => handler.Handle(
                new SetProviderRouteCommand(ProviderJobs.VerificationAirport, new[] { "mledoze" }, null), default));

            Assert.Equal(400, ex.StatusCode);
            Assert.Contains("Airport", ex.Message);
        }

        [Fact]
        public async Task Handle_VerificationKeyNamingASupportingSource_Upserts()
        {
            var airports = new Mock<IVerificationSource>();
            airports.SetupGet(s => s.Name).Returns("ourairports");
            airports.SetupGet(s => s.SupportedEntityTypes)
                    .Returns(new[] { VerificationEntityType.Airport });
            _registry.Setup(r => r.TryGet("ourairports")).Returns(airports.Object);

            await Build().Handle(new SetProviderRouteCommand(
                ProviderJobs.VerificationAirport, new[] { "ourairports" }, null), default);

            _repository.Verify(r => r.UpsertAsync(
                It.Is<ProviderRoute>(row => row.Providers == "ourairports"),
                It.IsAny<CancellationToken>()), Times.Once);
        }
```

Add `private readonly Mock<IVerificationSourceRegistry> _registry = new();` to the fixture and pass it into `Build()`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SetProviderRouteCommandTests"`
Expected: FAIL to compile — the handler takes no registry.

- [ ] **Step 3: Branch the validation by key kind**

Take `IVerificationSourceRegistry` as a constructor dependency and replace the single option loop with:

```csharp
            var options = await ValidateProvidersAsync(request, ct);
```

```csharp
        /// <summary>
        /// Checks every named provider and returns the option list to report back. A flight-search
        /// key has a static option list; a verification key's options are every registered source
        /// whose <c>SupportedEntityTypes</c> includes that key's entity type, which only the registry
        /// knows — so the two kinds are validated differently on purpose.
        /// </summary>
        private Task<IReadOnlyList<string>> ValidateProvidersAsync(
            SetProviderRouteCommand request, CancellationToken ct)
        {
            if (request.Job == ProviderJobs.FlightSearchRoute)
            {
                var options = ProviderJobs.OptionsFor(request.Job);
                foreach (var provider in request.Providers)
                {
                    if (!options.Contains(provider, StringComparer.Ordinal))
                    {
                        throw new DomainException(
                            $"'{provider}' is not a provider for '{request.Job}'. Options: {string.Join(", ", options)}.",
                            400);
                    }
                }
                return Task.FromResult(options);
            }

            var entityType = EntityTypeFor(request.Job);
            foreach (var name in request.Providers)
            {
                var source = _registry.TryGet(name);
                if (source is null)
                    throw new DomainException($"There is no verification source '{name}'.", 400);

                if (!source.SupportedEntityTypes.Contains(entityType))
                {
                    throw new DomainException(
                        $"Verification source '{name}' does not support {entityType}.", 400);
                }
            }

            IReadOnlyList<string> supported = _registry.Sources
                .Where(s => s.SupportedEntityTypes.Contains(entityType))
                .Select(s => s.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            return Task.FromResult(supported);
        }

        private static VerificationEntityType EntityTypeFor(string job) => job switch
        {
            ProviderJobs.VerificationAirport => VerificationEntityType.Airport,
            ProviderJobs.VerificationAirline => VerificationEntityType.Airline,
            ProviderJobs.VerificationCountry => VerificationEntityType.Country,
            _ => throw new InvalidOperationException($"'{job}' is not a verification routing key.")
        };
```

Use the returned `options` for the DTO's `Options` rather than calling `ProviderJobs.OptionsFor` again.

- [ ] **Step 4: Make the query report registry-derived options too**

`GetProviderRoutesQueryHandler` takes `IVerificationSourceRegistry` and, for a verification key, fills `Options` from the registry the same way. Extract the "supported source names for this entity type" expression into one private static method both classes can call, or duplicate it only if they are in different assemblies — they are not, so extract it into `ProviderJobs`:

```csharp
        /// <summary>
        /// The registered sources that can check <paramref name="entityType"/>. Lives here so the
        /// command's validation and the query's reported options cannot disagree about what is
        /// selectable.
        /// </summary>
        public static IReadOnlyList<string> SupportedSourceNames(
            IVerificationSourceRegistry registry, VerificationEntityType entityType) =>
            registry.Sources
                .Where(s => s.SupportedEntityTypes.Contains(entityType))
                .Select(s => s.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
```

and call it from both.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SetProviderRouteCommandTests"`
Expected: PASS, 7 tests.

- [ ] **Step 6: Add an endpoint test for all four keys**

In `AdminProviderEndpointTests`:

```csharp
        [Fact]
        public async Task GetProviders_ReturnsAllFourRoutingKeys()
        {
            var client = CreateAdminClient();

            var rows = await client.GetFromJsonAsync<List<AdminProviderRouteDto>>("/admin/providers");

            Assert.Equal(4, rows!.Count);
            Assert.Contains(rows, r => r.Job == "flight-search-route");
            Assert.Contains(rows, r => r.Job == "verification-airport");
            Assert.Contains(rows, r => r.Job == "verification-airline");
            Assert.Contains(rows, r => r.Job == "verification-country");
        }
```

- [ ] **Step 7: Regenerate the spec and run everything**

Run: `scripts/generate-openapi.sh` then `dotnet test`
Expected: PASS, and `spec/openapi.json` unchanged except for any description text you edited.

- [ ] **Step 8: Commit and open the PR**

```bash
git add FlightAssistant.Application FlightAssistant.API.Tests FlightAssistant.Application.Tests spec/openapi.json
git commit -m "feat(admin): validate verification source choices against the registry (#531)"
git push -u origin HEAD
gh pr create --base development --title "Runtime switch for verification default sources" \
  --body "Part 2 of #531. Verification:DefaultSources:* becomes runtime-switchable per entity type, read through IVerificationSourceDefaults by the #410 flight-tracking trigger and the #465 provisioner, falling back to configuration.

This is where cost steering actually bites: aeroapi bills \$0.015 per airline lookup against free ourairports/mledoze, and changing it previously took a restart. The write validates each source against IVerificationSourceRegistry, which configuration cannot do -- VerificationDefaultSources.Validate checks only the join grammar, which is why a pre-#401 aeroapi default would have completed every request straight to Error.

Deliberately does not reach the verification job, which uses each request's own stored sources.

Spec: docs/superpowers/specs/2026-09-19-provider-routing-switch-design.md"
```

---

# PR 3 — the map and the headroom numbers

Create a GitHub issue first ("Publish a map of which provider is called on which operation (#531 PR 3)"), then branch off `development`.

### Task 10: AeroDataBox quota from its own response headers

**Files:**
- Create: `FlightAssistant.Application/Interfaces/Providers/IProviderQuotaReader.cs`
- Create: `FlightAssistant.Infrastructure/ExternalServices/AeroDataBox/AeroDataBoxQuotaHolder.cs`
- Create: `FlightAssistant.Infrastructure/ExternalServices/AeroDataBox/AeroDataBoxQuotaHandler.cs`
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs` (the AeroDataBox client)
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroDataBox/AeroDataBoxQuotaHandlerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `IProviderQuotaReader.AeroDataBox → ProviderQuota?` where `ProviderQuota(int Remaining, int Limit, DateTimeOffset ObservedUtc)`. `AeroDataBoxQuotaHolder` implements it and exposes `Record(int? remaining, int? limit, DateTimeOffset now)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroDataBox
{
    public class AeroDataBoxQuotaHandlerTests
    {
        private static (HttpClient Client, AeroDataBoxQuotaHolder Holder) Build(
            HttpResponseMessage response, FakeTimeProvider time)
        {
            var holder = new AeroDataBoxQuotaHolder();
            var handler = new AeroDataBoxQuotaHandler(holder, time)
            {
                InnerHandler = new StubHandler(response),
            };
            return (new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, holder);
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpResponseMessage _response;
            public StubHandler(HttpResponseMessage response) => _response = response;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
                Task.FromResult(_response);
        }

        [Fact]
        public async Task SendAsync_RecordsTheQuotaHeaders()
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("x-ratelimit-requests-remaining", "1487");
            response.Headers.Add("x-ratelimit-requests-limit", "1505");
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

            var (client, holder) = Build(response, time);
            await client.GetAsync("anything");

            var quota = holder.AeroDataBox;
            Assert.NotNull(quota);
            Assert.Equal(1487, quota!.Remaining);
            Assert.Equal(1505, quota.Limit);
            Assert.Equal(time.GetUtcNow(), quota.ObservedUtc);
        }

        [Fact]
        public async Task AeroDataBox_BeforeAnyCall_IsNullNotZero()
        {
            // "Cannot tell" is a weaker claim than "nothing left" — the rule
            // AdminHealthMigrationsDto.Available already encodes. Reporting 0 here would show an
            // exhausted quota on every fresh deploy.
            var holder = new AeroDataBoxQuotaHolder();
            Assert.Null(holder.AeroDataBox);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task SendAsync_MissingHeaders_LeavesTheLastKnownValueAlone()
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
            var holder = new AeroDataBoxQuotaHolder();
            holder.Record(1487, 1505, time.GetUtcNow());

            var response = new HttpResponseMessage(HttpStatusCode.OK);
            var handler = new AeroDataBoxQuotaHandler(holder, time) { InnerHandler = new StubHandler(response) };
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

            await client.GetAsync("anything");

            Assert.Equal(1487, holder.AeroDataBox!.Remaining);
        }

        [Fact]
        public async Task SendAsync_TransportFailure_RethrowsUnchanged()
        {
            // Diagnostics must never alter or break the thing being diagnosed — the same rule
            // DataSourceObservingHandler states.
            var time = new FakeTimeProvider();
            var holder = new AeroDataBoxQuotaHolder();
            var handler = new AeroDataBoxQuotaHandler(holder, time)
            {
                InnerHandler = new ThrowingHandler(),
            };
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("anything"));
            Assert.Null(holder.AeroDataBox);
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
                throw new HttpRequestException("down");
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroDataBoxQuotaHandlerTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement the reader, holder and handler**

```csharp
namespace FlightAssistant.Application.Interfaces.Providers
{
    /// <param name="ObservedUtc">When this reading was taken. The figure is only as fresh as the last
    /// call this process made, and the panel says so rather than implying it is live.</param>
    public sealed record ProviderQuota(int Remaining, int Limit, DateTimeOffset ObservedUtc);

    /// <summary>
    /// Provider-reported quota, read off response headers rather than counted locally — the same rule
    /// that keeps AeroAPI spend coming from <c>/account/usage</c> and never from a local meter.
    /// </summary>
    public interface IProviderQuotaReader
    {
        /// <summary>
        /// The last quota AeroDataBox reported to <b>this process</b>, or null when it has made no
        /// call yet. Null means "cannot tell", never "nothing left", and it is per instance: a
        /// scaled-out deployment shows whichever instance served the request.
        /// </summary>
        ProviderQuota? AeroDataBox { get; }
    }
}
```

```csharp
using FlightAssistant.Application.Interfaces.Providers;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroDataBox
{
    /// <summary>
    /// Holds the last quota reading. A singleton with an atomic reference write, the same shape
    /// <c>FlightSyncHeartbeat</c> uses: a reader can never see a torn value, and there is no lock on
    /// the read path.
    /// </summary>
    public sealed class AeroDataBoxQuotaHolder : IProviderQuotaReader
    {
        private ProviderQuota? _current;

        public ProviderQuota? AeroDataBox => Volatile.Read(ref _current);

        /// <summary>Records a reading. A response missing either header changes nothing — the last
        /// known value is better than discarding it.</summary>
        public void Record(int? remaining, int? limit, DateTimeOffset now)
        {
            if (remaining is null || limit is null) return;
            Volatile.Write(ref _current, new ProviderQuota(remaining.Value, limit.Value, now));
        }
    }
}
```

```csharp
using System.Globalization;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroDataBox
{
    /// <summary>
    /// Records AeroDataBox's own quota headers off every response through its named client.
    ///
    /// <para>
    /// A <see cref="DelegatingHandler"/> for the reason <c>DataSourceObservingHandler</c> gives: it
    /// sees every call including ones in code not yet written, and cannot be forgotten at a new call
    /// site. It costs no extra request — these headers ride on traffic already being sent.
    /// </para>
    ///
    /// <para><b>It never changes the call it observes.</b> A transport failure is rethrown unchanged
    /// and nothing is recorded, because a failed call reports no quota.</para>
    /// </summary>
    public sealed class AeroDataBoxQuotaHandler : DelegatingHandler
    {
        public const string RemainingHeader = "x-ratelimit-requests-remaining";
        public const string LimitHeader = "x-ratelimit-requests-limit";

        private readonly AeroDataBoxQuotaHolder _holder;
        private readonly TimeProvider _time;

        public AeroDataBoxQuotaHandler(AeroDataBoxQuotaHolder holder, TimeProvider time)
        {
            _holder = holder;
            _time = time;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);

            try
            {
                _holder.Record(ReadInt(response, RemainingHeader), ReadInt(response, LimitHeader),
                    _time.GetUtcNow());
            }
            catch
            {
                // Diagnostics must not break the thing being diagnosed.
            }

            return response;
        }

        private static int? ReadInt(HttpResponseMessage response, string header) =>
            response.Headers.TryGetValues(header, out var values) &&
            int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }
}
```

- [ ] **Step 4: Register it on the AeroDataBox client**

In `DependencyInjection.cs`, add before `.ConfigurePrimaryHttpMessageHandler(...)` on the AeroDataBox client:

```csharp
            .AddHttpMessageHandler(sp => new AeroDataBoxQuotaHandler(
                sp.GetRequiredService<AeroDataBoxQuotaHolder>(),
                sp.GetRequiredService<TimeProvider>()))
```

and register the holder as a singleton behind both types:

```csharp
            services.AddSingleton<AeroDataBoxQuotaHolder>();
            services.AddSingleton<IProviderQuotaReader>(sp => sp.GetRequiredService<AeroDataBoxQuotaHolder>());
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroDataBoxQuotaHandlerTests"`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Application/Interfaces/Providers FlightAssistant.Infrastructure FlightAssistant.Infrastructure.Tests
git commit -m "feat(providers): read AeroDataBox quota off its own response headers (#531)"
```

---

### Task 11: The authored operation map and its completeness guard

**Files:**
- Create: `FlightAssistant.Application/Providers/ProviderOperationMap.cs`
- Test: `FlightAssistant.Infrastructure.Tests/Providers/ProviderOperationMapCompletenessTests.cs`

**Interfaces:**
- Consumes: `ProviderJobs`.
- Produces: `ProviderOperationMap.Entries` of `ProviderOperation(string Trigger, string Operation, IReadOnlyList<string> Providers, string? RoutingKey, string? FixedReason, string ListPrice)`.

- [ ] **Step 1: Write the map**

```csharp
namespace FlightAssistant.Application.Providers
{
    /// <param name="RoutingKey">The <see cref="ProviderJobs"/> key that decides this row, or null
    /// when the row is fixed.</param>
    /// <param name="FixedReason">Why this row cannot be changed, or null when it can. A fixed row is
    /// on the map precisely so the reason is visible rather than buried in a comment.</param>
    /// <param name="ListPrice">Authored, with its own date. Neither provider exposes a price list
    /// through an API, so this cannot be derived; stamping the date makes staleness visible instead
    /// of silent, the same posture as a null <c>dataAsOfUtc</c> on Azure cost.</param>
    /// <param name="Note">A consequence a reader needs before acting on this row, distinct from
    /// <paramref name="FixedReason"/>: a switchable row can still carry one. Optional.</param>
    public sealed record ProviderOperation(
        string Trigger,
        string Operation,
        IReadOnlyList<string> Providers,
        string? RoutingKey,
        string? FixedReason,
        string ListPrice,
        string? Note = null);

    /// <summary>
    /// Which provider is called on which operation. Authored rather than scanned, following
    /// <c>DataSourceCatalog</c>'s own precedent: the trigger prose and the fixed-reasons cannot be
    /// derived from the code, and <c>ProviderOperationMapCompletenessTests</c> is what keeps an
    /// authored list honest.
    /// </summary>
    public static class ProviderOperationMap
    {
        public const string AeroApi = ProviderJobs.AeroApi;
        public const string AeroDataBox = ProviderJobs.AeroDataBox;
        public const string OurAirports = "ourairports";
        public const string Mledoze = "mledoze";

        public static readonly IReadOnlyList<ProviderOperation> Entries =
        [
            new("Tracking a flight", "POST /flights",
                [], null,
                "Tracking itself calls no provider. A verification source is called only when the " +
                "airline, an airport or a country is missing and has to be provisioned (#465), which " +
                "the three verification rows below decide.",
                "$0.00"),

            new("Searching", "GET /flights/search — both airports, no flight number",
                [AeroDataBox], ProviderJobs.FlightSearchRoute, null,
                "AeroDataBox $0.00 (free tier, 2 calls per search) · AeroAPI $0.020/page, list as of 2026-09-19",
                Note:
                "Routing this to AeroAPI puts it back behind AeroApiFlightSearchBudgetDecorator, " +
                "which refuses from SoftLimit (80% of AeroApi:MonthlyBudgetUsd) onward — so this flip " +
                "can turn route searches into 503s at exactly the moment a cost-conscious operator " +
                "reaches for it. Cached searches are still answered."),

            new("Searching", "GET /flights/search — anchored on a flight number",
                [AeroApi], null,
                "AeroDataBox resolves a marketing number to its operator (6E4120 -> TK 1028), so " +
                "routing this there could change which flight a traveler tracks (#422). A correctness " +
                "rule, not a cost preference.",
                "AeroAPI $0.020/page, list as of 2026-09-19"),

            new("Searching", "GET /airports/{id} — origin timezone, AeroAPI searches only",
                [AeroApi], null,
                "Follows whichever provider serves the search. AeroDataBox needs no timezone lookup: " +
                "its rows carry local times with the UTC offset embedded. Cached 365 days.",
                "AeroAPI $0.015/call, list as of 2026-09-19"),

            new("Status sync", "GET /flights/{ident} — Fast and Live modes",
                [AeroApi], null,
                "The only implementation. AeroDataBox's flight-number endpoint carries no gate and no " +
                "baggage claim, so adopting it would lose fields FlightStatusEvent.CreateDetailChange " +
                "records (#351).",
                "AeroAPI $0.005/page, list as of 2026-09-19"),

            new("Status sync", "GET /schedules — Schedule mode's daily check (#444)",
                [AeroApi], null,
                "Runs as a flight-number search, so it follows that row and cannot be routed to " +
                "AeroDataBox's free tier for the same reason.",
                "AeroAPI $0.020/page, list as of 2026-09-19"),

            new("Verification", "Airport checks",
                [OurAirports], ProviderJobs.VerificationAirport, null,
                "ourairports $0.00 · aeroapi $0.015/lookup, list as of 2026-09-19"),

            new("Verification", "Airline checks",
                [AeroApi], ProviderJobs.VerificationAirline, null,
                "aeroapi $0.015/lookup, list as of 2026-09-19 — the most steerable cost here"),

            new("Verification", "Country checks",
                [Mledoze], ProviderJobs.VerificationCountry, null,
                "mledoze $0.00, list as of 2026-09-19"),

            new("Budget and usage", "GET /account/usage",
                [AeroApi], null,
                "The figure the usage gauge and the budget guard both read. FlightAware bills this " +
                "endpoint at $0.00, and one cached read per 10 minutes serves both.",
                "AeroAPI $0.00"),
        ];
    }
}
```

- [ ] **Step 2: Write the failing completeness guard**

```csharp
using System.Reflection;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Providers;

namespace FlightAssistant.Infrastructure.Tests.Providers
{
    /// <summary>
    /// Keeps <see cref="ProviderOperationMap"/> honest. An authored map rots, and a stale map is
    /// worse than no map: it states with apparent authority that a provider is not called.
    ///
    /// <para>
    /// <b>The obligation rule is structural, not incidental.</b> A type implementing one of the
    /// provider interfaces that does <i>not</i> take one of those interfaces as a constructor
    /// parameter is a real provider — decorators, selectors and caches wrap one, real providers do
    /// not — and every real provider owes at least one map row. Mutation-tested both ways when it
    /// was written: deleting a row turns it red, and adding a provider turns it red.
    /// </para>
    /// </summary>
    public class ProviderOperationMapCompletenessTests
    {
        private static readonly Type[] ProviderInterfaces =
        [
            typeof(IFlightSearchProvider),
            typeof(IFlightStatusProvider),
            typeof(IVerificationSource),
        ];

        [Fact]
        public void EveryRealProviderAppearsOnTheMap()
        {
            var named = ProviderOperationMap.Entries
                .SelectMany(e => e.Providers)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = new List<string>();

            foreach (var type in RealProviders())
            {
                // A provider's map name is the catalog/source name it is known by, which is not
                // derivable from its type name -- so match on the type's own name containing a named
                // provider, which is how every provider in this codebase is actually named
                // (AeroApiFlightStatusProvider -> aeroapi, MledozeCountriesVerificationSource ->
                // mledoze, OurAirportsVerificationSource -> ourairports).
                if (!named.Any(name => type.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    missing.Add(type.Name);
            }

            Assert.True(missing.Count == 0,
                "These providers appear on no ProviderOperationMap row: " + string.Join(", ", missing) +
                ". Add a row, or -- if the provider genuinely serves no operation -- say so in its row's FixedReason.");
        }

        /// <summary>
        /// Concrete provider implementations, excluding every wrapper. A wrapper is identified by
        /// taking one of the provider interfaces as a constructor parameter, which is what a
        /// decorator, selector or caching layer does and a real provider never does.
        /// </summary>
        private static IEnumerable<Type> RealProviders()
        {
            var assembly = typeof(FlightAssistant.Infrastructure.DependencyInjection).Assembly;

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!ProviderInterfaces.Any(i => i.IsAssignableFrom(type))) continue;

                var wrapsAProvider = type.GetConstructors()
                    .SelectMany(c => c.GetParameters())
                    .Any(p => ProviderInterfaces.Any(i => i.IsAssignableFrom(p.ParameterType)));

                if (!wrapsAProvider) yield return type;
            }
        }

        [Fact]
        public void EverySwitchableRowNamesAKnownRoutingKey()
        {
            foreach (var entry in ProviderOperationMap.Entries.Where(e => e.RoutingKey is not null))
                Assert.Contains(entry.RoutingKey!, ProviderJobs.All);
        }

        [Fact]
        public void EveryRowIsEitherSwitchableOrExplainsWhyNot()
        {
            // A row with neither a routing key nor a reason is the failure mode this map exists to
            // prevent: a provider call nobody can change and nobody has justified.
            foreach (var entry in ProviderOperationMap.Entries)
            {
                Assert.True(
                    entry.RoutingKey is not null || !string.IsNullOrWhiteSpace(entry.FixedReason),
                    $"'{entry.Operation}' is neither switchable nor explained.");
            }
        }

        [Fact]
        public void EveryRoutingKeyAppearsOnExactlyOneRow()
        {
            foreach (var job in ProviderJobs.All)
                Assert.Single(ProviderOperationMap.Entries, e => e.RoutingKey == job);
        }

        [Fact]
        public void TheRouteSearchRowWarnsThatRoutingToAeroApiReintroducesTheBudgetRefusal()
        {
            // Spec section 6.1. This is the one consequence of a flip that an operator cannot infer
            // from the row itself, and it bites precisely when they would reach for the switch, so it
            // is asserted rather than left to survive a future edit of the prose.
            var row = Assert.Single(ProviderOperationMap.Entries,
                e => e.RoutingKey == ProviderJobs.FlightSearchRoute);

            Assert.NotNull(row.Note);
            Assert.Contains("SoftLimit", row.Note!);
        }
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ProviderOperationMapCompletenessTests"`
Expected: FAIL to compile before Step 1's file exists; after it, all four should pass. If `EveryRealProviderAppearsOnTheMap` fails, read the failure message — it names the provider, and the fix is a map row, not a change to the test.

- [ ] **Step 4: Mutation-test the guard, both directions**

This step is not optional: a sweep-style guard that passes in both directions reads as coverage while providing none.

1. Temporarily delete the "Airline checks" row. Run the filter above. Expected: `EveryRealProviderAppearsOnTheMap` or `EveryRoutingKeyAppearsOnExactlyOneRow` goes **red**. Restore the row.
2. Temporarily add `internal sealed class OpenSkyFlightStatusProvider : IFlightStatusProvider` to the Infrastructure assembly with a throwing method body and no provider-typed constructor parameter. Run the filter. Expected: `EveryRealProviderAppearsOnTheMap` goes **red** naming it. Delete the class.
3. Record both observed results in the commit message.

- [ ] **Step 5: Commit**

```bash
git add FlightAssistant.Application/Providers/ProviderOperationMap.cs \
  FlightAssistant.Infrastructure.Tests/Providers/ProviderOperationMapCompletenessTests.cs
git commit -m "feat(providers): the authored operation map and its completeness guard (#531)"
```

---

### Task 12: Serve the map with headroom

**Files:**
- Create: `FlightAssistant.Contracts/Responses/Admin/AdminProviderMapDto.cs`
- Modify: `FlightAssistant.Application/Queries/Admin/Providers/GetProviderRoutesQuery.cs`
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminProviderEndpoints.cs`
- Test: `FlightAssistant.API.Tests/Integration/AdminProviderEndpointTests.cs`

**Interfaces:**
- Consumes: `ProviderOperationMap.Entries`, `IProviderRouting`, `IProviderQuotaReader`, `IProviderBudgetGuard`, `IAeroApiUsageCache` (whichever the existing `GetFlightDataUsageQuery` uses for cached spend — read it and reuse the same dependency).
- Produces: `AdminProviderMapDto(IReadOnlyList<AdminProviderRouteDto> Routes, IReadOnlyList<AdminProviderOperationDto> Operations, IReadOnlyList<AdminProviderHeadroomDto> Headroom)`.

- [ ] **Step 1: Create the response DTOs**

```csharp
namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <param name="RoutingKey">Null when this operation's provider is fixed.</param>
    /// <param name="FixedReason">Why it is fixed, or null when it is switchable.</param>
    /// <param name="Note">A consequence to read before acting on this row. Unlike
    /// <paramref name="FixedReason"/>, a switchable row can carry one.</param>
    public sealed record AdminProviderOperationDto(
        string Trigger,
        string Operation,
        IReadOnlyList<string> Providers,
        string? RoutingKey,
        string? FixedReason,
        string ListPrice,
        string? Note);

    /// <param name="QuotaRemaining">Null means unknown — this process has made no call yet — never
    /// "nothing left". Per instance, so a scaled-out deployment shows whichever instance answered.</param>
    /// <param name="ObservedUtc">When the figure was read. Null with a null quota.</param>
    /// <param name="BudgetState">"Normal", "SoftLimit" or "Exhausted" for a metered provider; null
    /// for a free one.</param>
    public sealed record AdminProviderHeadroomDto(
        string Provider,
        string? BudgetState,
        decimal? SpentUsd,
        decimal? BudgetUsd,
        int? QuotaRemaining,
        int? QuotaLimit,
        DateTimeOffset? ObservedUtc);

    /// <summary>
    /// What <c>GET /admin/providers</c> returns: the switchable decisions, the full call map
    /// including fixed rows, and each provider's remaining headroom.
    /// </summary>
    public sealed record AdminProviderMapDto(
        IReadOnlyList<AdminProviderRouteDto> Routes,
        IReadOnlyList<AdminProviderOperationDto> Operations,
        IReadOnlyList<AdminProviderHeadroomDto> Headroom);
}
```

- [ ] **Step 2: Write the failing endpoint test**

```csharp
        [Fact]
        public async Task GetProviders_ReturnsTheOperationMapAndHeadroom()
        {
            var client = CreateAdminClient();

            var map = await client.GetFromJsonAsync<AdminProviderMapDto>("/admin/providers");

            Assert.NotNull(map);
            Assert.Equal(4, map!.Routes.Count);

            // Every trigger the operator asked about is represented.
            Assert.Contains(map.Operations, o => o.Operation.StartsWith("POST /flights"));
            Assert.Contains(map.Operations, o => o.Trigger == "Status sync");
            Assert.Contains(map.Operations, o => o.Trigger == "Verification");

            // A fixed row explains itself rather than just being absent from the switch list.
            var flightNumberSearch = Assert.Single(map.Operations,
                o => o.Operation.Contains("anchored on a flight number"));
            Assert.Null(flightNumberSearch.RoutingKey);
            Assert.Contains("6E4120", flightNumberSearch.FixedReason!);

            // Quota is unknown, not zero, in a test host that has called AeroDataBox never.
            var aeroDataBox = Assert.Single(map.Headroom, h => h.Provider == "aerodatabox");
            Assert.Null(aeroDataBox.QuotaRemaining);
        }
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~AdminProviderEndpointTests"`
Expected: FAIL — the endpoint returns a list, not `AdminProviderMapDto`.

- [ ] **Step 4: Widen the query**

Change `GetProviderRoutesQuery` to `IRequest<AdminProviderMapDto>`. `GetProviderRoutesQueryHandler` gains four constructor dependencies alongside the `IProviderRouting`, `IProviderRouteDefaults` and `IVerificationSourceRegistry` it already has — the budget guard (`_budget`), the cached usage reader (`_usage`), the budget options (`_budgetOptions`, for `MonthlyBudgetUsd`) and `IProviderQuotaReader` (`_quota`) — resolved exactly as `GetFlightDataUsageQueryHandler` resolves its own. Keep the existing route-building loop for `Routes`. Add:

```csharp
            var operations = ProviderOperationMap.Entries
                .Select(e => new AdminProviderOperationDto(
                    e.Trigger, e.Operation, e.Providers, e.RoutingKey, e.FixedReason, e.ListPrice, e.Note))
                .ToList();

            var headroom = new List<AdminProviderHeadroomDto>
            {
                await BuildAeroApiHeadroomAsync(ct),
                BuildAeroDataBoxHeadroom(),
            };

            return new AdminProviderMapDto(routes, operations, headroom);
```

```csharp
        /// <summary>
        /// AeroAPI headroom from the guard and the cached usage report — the same 10-minute cached
        /// upstream read the usage gauge and the budget guard already share, so this costs no extra
        /// metered call and no extra throttle slot. A failed read reports nulls rather than zeros:
        /// "cannot tell" is not "nothing spent".
        /// </summary>
        private async Task<AdminProviderHeadroomDto> BuildAeroApiHeadroomAsync(CancellationToken ct)
        {
            try
            {
                var state = await _budget.GetStateAsync(ct);
                var usage = await _usage.GetCurrentMonthAsync(ct);
                return new AdminProviderHeadroomDto(
                    Provider: ProviderJobs.AeroApi,
                    BudgetState: state.ToString(),
                    SpentUsd: usage?.TotalCostUsd,
                    BudgetUsd: _budgetOptions.MonthlyBudgetUsd,
                    QuotaRemaining: null,
                    QuotaLimit: null,
                    ObservedUtc: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new AdminProviderHeadroomDto(
                    ProviderJobs.AeroApi, null, null, _budgetOptions.MonthlyBudgetUsd, null, null, null);
            }
        }

        private AdminProviderHeadroomDto BuildAeroDataBoxHeadroom()
        {
            var quota = _quota.AeroDataBox;
            return new AdminProviderHeadroomDto(
                Provider: ProviderJobs.AeroDataBox,
                BudgetState: null,
                SpentUsd: null,
                BudgetUsd: null,
                QuotaRemaining: quota?.Remaining,
                QuotaLimit: quota?.Limit,
                ObservedUtc: quota?.ObservedUtc);
        }
```

Read `GetFlightDataUsageQuery` first and reuse its exact cached-usage dependency and method name rather than inventing `GetCurrentMonthAsync`; the point is that both routes share one cache entry.

- [ ] **Step 5: Update the endpoint's declared type and description**

```csharp
            .Produces<AdminProviderMapDto>(200)
```

and extend the description:

```csharp
                "...Also returns the full call map, including the operations whose provider is fixed " +
                "and why, and each provider's remaining headroom. AeroDataBox quota is null until " +
                "this instance has called it — unknown, not exhausted — and is per instance. List " +
                "prices are authored and carry their own date; neither provider publishes a price " +
                "list through an API."
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~AdminProviderEndpointTests"`
Expected: PASS. The earlier tests asserting a `List<AdminProviderRouteDto>` need updating to read `map.Routes` — do that rather than deleting them.

- [ ] **Step 7: Regenerate the spec and run everything**

Run: `scripts/generate-openapi.sh` then `dotnet test`
Expected: PASS.

- [ ] **Step 8: Update CLAUDE.md**

Add a Key Patterns paragraph after the AeroDataBox chain-position one, in this repository's voice: what the switch is, that `provider_routes` is a sibling of `job_switches` and why, that configuration is a hard ceiling, that the search cache key now carries the provider and the prefix-flush step is retired, that a verification flip does not reach already-queued requests, and that list prices are authored with a date. Also add `ProviderRoute` to the audit-exclusion sentence and the four new keys to the configuration table if any gain a config key.

- [ ] **Step 9: Commit and open the PR**

```bash
git add FlightAssistant.Contracts FlightAssistant.Application FlightAssistant.API \
  FlightAssistant.API.Tests spec/openapi.json CLAUDE.md
git commit -m "feat(admin): serve the provider operation map with list prices and headroom (#531)"
git push -u origin HEAD
gh pr create --base development --title "Publish the provider operation map with prices and headroom" \
  --body "Part 3 of #531. GET /admin/providers now returns the full call map -- which provider is called on which operation, grouped by trigger -- including the rows whose provider is fixed and why, plus each provider's remaining headroom.

The map is authored rather than scanned, following DataSourceCatalog's precedent, and ProviderOperationMapCompletenessTests keeps it honest: every real provider owes a row, where a real provider is one that does not take a provider interface as a constructor parameter. Mutation-tested both directions.

AeroDataBox quota comes off its own x-ratelimit headers through a DelegatingHandler, costing no extra call. It is null until this instance has called AeroDataBox -- unknown, never zero -- and is per instance. List prices are authored with a stamped date; neither provider publishes a price list through an API.

Spec: docs/superpowers/specs/2026-09-19-provider-routing-switch-design.md"
```

---

## Follow-up, not in scope

- The admin panel UI is a separate repository and a separate PR: four switch controls, the map table grouped by trigger, and the headroom figures. It must render a null quota as "unknown", never 0.
- `ProviderJobs.OptionsFor` returning `[]` for verification keys is a small wart — the registry is the real source. If a third kind of key ever appears, replace `OptionsFor` with a single registry-backed method rather than adding a third branch.
