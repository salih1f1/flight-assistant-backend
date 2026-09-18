# Data-Source Inventory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put every outside dependency on the system health page with a real status, a real "last checked" timestamp, a sentence saying what was verified, a link out, and an operator-triggered "Check now".

**Architecture:** A source catalog enumerates all nine dependencies. Status is recorded passively off existing call paths (a `DelegatingHandler` on each named `HttpClient`, plus the database, Redis and APNs paths) into a buffered writer that persists one throttled row per source. A pure evaluator derives the displayed status. Two new endpoints read the table and run a deliberate probe.

**Tech Stack:** .NET 10, EF Core (Npgsql), MediatR, xUnit + Moq, minimal APIs. Admin panel: React 19, TypeScript, Vite, Vitest + Testing Library, orval-generated client, Tailwind + shadcn/ui.

**Spec:** `docs/superpowers/specs/2026-09-18-data-source-inventory-design.md`

## Global Constraints

- **Error text never carries an upstream exception message.** Fixed categories plus facts this code controls (source name, HTTP status code). Follows `VerificationSourceDownloader`'s existing rule — a proxy error can name an internal address, a parser error can quote raw record content.
- **Anything exception-derived beyond its category is non-Production only**, matching `AdminHealthReportBuilder`'s `includeDiagnostics` convention.
- **`Unknown` is never rendered as healthy.** Same rule the Migrations panel sets: "can't tell" ≠ "nothing wrong".
- **No new outbound traffic.** Every observation seam is an existing call path. Only the probe endpoint initiates a call, and only when an operator presses it.
- **Column names are snake_case**, matching `verification_requests`.
- **Enum-like columns store names, not ints**, so a psql session can read them — matching `VerificationRequestConfiguration`.
- Nullable reference types are enabled. XML doc comments on public types explain *why*, not *what*, matching the surrounding code.
- Tests are xUnit: `[Fact]` / `[Theory]`, `Assert.Equal(expected, actual)`.

---

# Phase 1 — Backend (PR 1)

### Task 1: Application contracts

**Files:**
- Create: `FlightAssistant.Application/DataSources/DataSourceOutcome.cs`
- Create: `FlightAssistant.Application/DataSources/DataSourceStatusValue.cs`
- Create: `FlightAssistant.Application/DataSources/DataSourceDescriptor.cs`
- Create: `FlightAssistant.Application/Interfaces/DataSources/IDataSourceCatalog.cs`
- Create: `FlightAssistant.Application/Interfaces/DataSources/IDataSourceObserver.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: everything below. Every later task depends on these exact names.

- [ ] **Step 1: Create the two enums**

```csharp
namespace FlightAssistant.Application.DataSources
{
    /// <summary>
    /// What one observation of a source saw. <see cref="Unauthorized"/> is split out from
    /// <see cref="Failed"/> deliberately: it is the state the old AeroAPI health check reported as
    /// Healthy ("Reachable — HTTP 401"), and it needs a different remedy (rotate a key) from a
    /// network failure (wait). <see cref="RateLimited"/> is likewise not a fault of ours.
    /// </summary>
    public enum DataSourceOutcome { Ok, Failed, Unauthorized, RateLimited, NotConfigured }
}
```

```csharp
namespace FlightAssistant.Application.DataSources
{
    /// <summary>
    /// The status a source's row displays. <see cref="Unknown"/> is first-class: a source that has
    /// never been observed must not render as healthy.
    /// </summary>
    public enum DataSourceStatusValue { Unknown, NotConfigured, Ok, Degraded, Unhealthy }
}
```

- [ ] **Step 2: Create the descriptor**

```csharp
namespace FlightAssistant.Application.DataSources
{
    /// <summary>Whether a missing or failing source is a fault.</summary>
    public enum DataSourceCriticality { Required, Optional }

    /// <summary>How the health page groups source rows.</summary>
    public enum DataSourceKind { Database, FlightData, ReferenceData, Notifications, Telemetry, Infrastructure }

    /// <summary>
    /// One outside dependency, as the health page describes it. <see cref="WhatIsChecked"/> and
    /// <see cref="ExternalUrl"/> live here rather than on a health check because they are facts
    /// about the source itself, and because a single registry is what makes the catalog
    /// completeness test possible (see <c>DataSourceCatalogTests</c>).
    /// </summary>
    /// <param name="Key">Stable id, used in URLs and as the status table's primary key.</param>
    /// <param name="WhatIsChecked">One author-written sentence, rendered verbatim to the operator.</param>
    /// <param name="HttpClientName">The named <see cref="HttpClient"/> this source is reached
    /// through, or null for a source not reached over HTTP. The completeness test matches on this.</param>
    /// <param name="ProbeCostNote">Null when probing is free; otherwise the warning shown before
    /// the call goes out.</param>
    public sealed record DataSourceDescriptor(
        string Key,
        string DisplayName,
        DataSourceKind Kind,
        DataSourceCriticality Criticality,
        string WhatIsChecked,
        string? ExternalUrl,
        string? HttpClientName,
        bool SupportsProbe,
        string? ProbeCostNote);
}
```

- [ ] **Step 3: Create the two interfaces**

```csharp
using FlightAssistant.Application.DataSources;

namespace FlightAssistant.Application.Interfaces.DataSources
{
    /// <summary>Every outside dependency the application has. Registered as a singleton.</summary>
    public interface IDataSourceCatalog
    {
        IReadOnlyList<DataSourceDescriptor> Sources { get; }

        DataSourceDescriptor? TryGet(string key);
    }
}
```

```csharp
using FlightAssistant.Application.DataSources;

namespace FlightAssistant.Application.Interfaces.DataSources
{
    /// <summary>
    /// Records what one call to a source saw. Implementations must not block the caller and must
    /// never throw: an observation is diagnostics, and a failure to record one must not fail the
    /// call it was describing.
    /// </summary>
    public interface IDataSourceObserver
    {
        void Record(string key, DataSourceOutcome outcome, DateTimeOffset at, string? detail = null);
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build FlightAssistant.Application/FlightAssistant.Application.csproj`
Expected: succeeds, no warnings.

- [ ] **Step 5: Commit**

```bash
git add FlightAssistant.Application/DataSources FlightAssistant.Application/Interfaces/DataSources
git commit -m "feat(health): add data-source catalog and observer contracts (#460)"
```

---

### Task 2: Status derivation

The single rule that turns a stored row into a displayed status. Pure, so it is table-tested without a database.

**Files:**
- Create: `FlightAssistant.Application/DataSources/DataSourceStatusEvaluator.cs`
- Test: `FlightAssistant.Application.Tests/DataSources/DataSourceStatusEvaluatorTests.cs`

**Interfaces:**
- Consumes: `DataSourceOutcome`, `DataSourceStatusValue`, `DataSourceCriticality` (Task 1).
- Produces: `DataSourceStatusEvaluator.Evaluate(DataSourceOutcome? lastOutcome, DateTimeOffset? lastOkUtc, DataSourceCriticality criticality, DateTimeOffset now) → DataSourceStatusValue`, and `public static readonly TimeSpan RecentSuccessWindow`.

- [ ] **Step 1: Write the failing tests**

```csharp
using FlightAssistant.Application.DataSources;
using Xunit;

namespace FlightAssistant.Application.Tests.DataSources
{
    public class DataSourceStatusEvaluatorTests
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void NeverObserved_IsUnknown_NotOk()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                lastOutcome: null, lastOkUtc: null, DataSourceCriticality.Required, Now);

            Assert.Equal(DataSourceStatusValue.Unknown, status);
        }

        [Fact]
        public void NotConfigured_StaysNotConfigured_NotUnhealthy()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.NotConfigured, lastOkUtc: null, DataSourceCriticality.Optional, Now);

            Assert.Equal(DataSourceStatusValue.NotConfigured, status);
        }

        [Fact]
        public void Ok_IsOk()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.Ok, Now, DataSourceCriticality.Required, Now);

            Assert.Equal(DataSourceStatusValue.Ok, status);
        }

        // The case that motivated the whole issue: the old aeroapi check called a 401 "Healthy".
        [Fact]
        public void Unauthorized_OnRequiredSource_IsUnhealthy_NeverOk()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.Unauthorized, lastOkUtc: null, DataSourceCriticality.Required, Now);

            Assert.Equal(DataSourceStatusValue.Unhealthy, status);
        }

        [Fact]
        public void Unauthorized_OnOptionalSource_IsDegraded()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.Unauthorized, lastOkUtc: null, DataSourceCriticality.Optional, Now);

            Assert.Equal(DataSourceStatusValue.Degraded, status);
        }

        [Fact]
        public void RateLimited_IsDegraded_NotUnhealthy()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.RateLimited, Now, DataSourceCriticality.Required, Now);

            Assert.Equal(DataSourceStatusValue.Degraded, status);
        }

        [Fact]
        public void Failed_OnRequiredSource_WithRecentSuccess_IsDegraded()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.Failed,
                lastOkUtc: Now.AddMinutes(-5),
                DataSourceCriticality.Required,
                Now);

            Assert.Equal(DataSourceStatusValue.Degraded, status);
        }

        [Fact]
        public void Failed_OnRequiredSource_WithNoRecentSuccess_IsUnhealthy()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.Failed,
                lastOkUtc: Now - DataSourceStatusEvaluator.RecentSuccessWindow.Add(TimeSpan.FromMinutes(1)),
                DataSourceCriticality.Required,
                Now);

            Assert.Equal(DataSourceStatusValue.Unhealthy, status);
        }

        [Fact]
        public void Failed_OnOptionalSource_IsDegraded_EvenWithNoRecentSuccess()
        {
            var status = DataSourceStatusEvaluator.Evaluate(
                DataSourceOutcome.Failed, lastOkUtc: null, DataSourceCriticality.Optional, Now);

            Assert.Equal(DataSourceStatusValue.Degraded, status);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FlightAssistant.Application.Tests --filter DataSourceStatusEvaluatorTests`
Expected: FAIL — `DataSourceStatusEvaluator` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace FlightAssistant.Application.DataSources
{
    /// <summary>
    /// Turns a stored observation into the status the page shows. Pure and static so the whole
    /// rule is table-testable without a database or a host — this is the one place the
    /// Degraded/Unhealthy line is drawn, and the admin panel renders what it is given rather than
    /// re-deriving it.
    /// </summary>
    public static class DataSourceStatusEvaluator
    {
        /// <summary>
        /// How recently a success excuses a failure. A required source that failed once but
        /// succeeded within the window is Degraded rather than Unhealthy: a single transient
        /// failure among successful calls is worth showing and not worth alarming about.
        /// </summary>
        public static readonly TimeSpan RecentSuccessWindow = TimeSpan.FromHours(1);

        public static DataSourceStatusValue Evaluate(
            DataSourceOutcome? lastOutcome,
            DateTimeOffset? lastOkUtc,
            DataSourceCriticality criticality,
            DateTimeOffset now)
        {
            if (lastOutcome is null) return DataSourceStatusValue.Unknown;

            return lastOutcome switch
            {
                DataSourceOutcome.NotConfigured => DataSourceStatusValue.NotConfigured,
                DataSourceOutcome.Ok => DataSourceStatusValue.Ok,
                DataSourceOutcome.RateLimited => DataSourceStatusValue.Degraded,
                _ when criticality == DataSourceCriticality.Optional => DataSourceStatusValue.Degraded,
                _ when HasRecentSuccess(lastOkUtc, now) => DataSourceStatusValue.Degraded,
                _ => DataSourceStatusValue.Unhealthy,
            };
        }

        private static bool HasRecentSuccess(DateTimeOffset? lastOkUtc, DateTimeOffset now) =>
            lastOkUtc is { } ok && now - ok <= RecentSuccessWindow;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FlightAssistant.Application.Tests --filter DataSourceStatusEvaluatorTests`
Expected: PASS, 9 tests.

- [ ] **Step 5: Break it on purpose to prove the tests bite**

Temporarily change the `DataSourceOutcome.Ok` arm to `DataSourceStatusValue.Unknown` and re-run. Expected: `Ok_IsOk` fails. Restore it and re-run to green before committing.

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Application/DataSources/DataSourceStatusEvaluator.cs FlightAssistant.Application.Tests/DataSources
git commit -m "feat(health): derive a data source's displayed status (#460)"
```

---

### Task 3: The catalog and its completeness test

**Files:**
- Create: `FlightAssistant.Infrastructure/DataSources/DataSourceCatalog.cs`
- Test: `FlightAssistant.Infrastructure.Tests/DataSources/DataSourceCatalogTests.cs`

**Interfaces:**
- Consumes: `IDataSourceCatalog`, `DataSourceDescriptor` (Task 1).
- Produces: `DataSourceCatalog` (singleton) and the public key constants `DataSourceKeys.Postgres`, `.AeroApi`, `.AeroDataBox`, `.OurAirports`, `.Mledoze`, `.GitHub`, `.Redis`, `.Apns`, `.AppInsights`.

- [ ] **Step 1: Write the failing tests**

```csharp
using FlightAssistant.Application.DataSources;
using FlightAssistant.Infrastructure.DataSources;
using Xunit;

namespace FlightAssistant.Infrastructure.Tests.DataSources
{
    public class DataSourceCatalogTests
    {
        private static readonly DataSourceCatalog Catalog = new();

        [Fact]
        public void EveryKeyIsUnique()
        {
            var keys = Catalog.Sources.Select(s => s.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void EverySourceExplainsWhatIsChecked()
        {
            Assert.All(Catalog.Sources, s => Assert.False(string.IsNullOrWhiteSpace(s.WhatIsChecked)));
        }

        /// <summary>
        /// The test that earns its keep. Six sources were invisible on the health page because
        /// nothing forced a new integration to appear there. This fails the build when a named
        /// HttpClient is registered with no catalog entry, so source #12 cannot repeat the story.
        /// </summary>
        [Fact]
        public void EveryNamedHttpClientHasACatalogEntry()
        {
            var registered = NamedHttpClients.All;
            var catalogued = Catalog.Sources
                .Where(s => s.HttpClientName is not null)
                .Select(s => s.HttpClientName!)
                .ToHashSet(StringComparer.Ordinal);

            var missing = registered.Where(name => !catalogued.Contains(name)).ToList();

            Assert.True(
                missing.Count == 0,
                $"These named HttpClients have no DataSourceCatalog entry, so they would be " +
                $"invisible on the health page: {string.Join(", ", missing)}");
        }

        [Fact]
        public void AProbeThatCostsMoneySaysSo()
        {
            foreach (var source in Catalog.Sources.Where(s => s.SupportsProbe && s.ProbeCostNote is not null))
                Assert.False(string.IsNullOrWhiteSpace(source.ProbeCostNote));
        }

        [Fact]
        public void TryGet_ReturnsNull_ForAnUnknownKey()
        {
            Assert.Null(Catalog.TryGet("not-a-source"));
        }
    }
}
```

- [ ] **Step 2: Add the `NamedHttpClients` list**

Create `FlightAssistant.Infrastructure/DataSources/NamedHttpClients.cs`. It names the clients `DependencyInjection.AddInfrastructure` registers, in one place both DI and the test read:

```csharp
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using FlightAssistant.Infrastructure.ExternalServices.GitHub;
using FlightAssistant.Infrastructure.ExternalServices.Verification;

namespace FlightAssistant.Infrastructure.DataSources
{
    /// <summary>
    /// Every named <see cref="HttpClient"/> this application registers. Kept beside the catalog so
    /// <c>DataSourceCatalogTests.EveryNamedHttpClientHasACatalogEntry</c> can assert the two agree:
    /// adding a client here without a catalog entry fails the build, which is what stops a new
    /// outside dependency from being invisible on the health page.
    /// </summary>
    public static class NamedHttpClients
    {
        public const string AeroApi = "AeroApi";

        public static readonly IReadOnlyList<string> All =
        [
            AeroApi,
            AeroDataBoxFlightSearchProvider.HttpClientName,
            GitHubCiRunProvider.ApiClientName,
            GitHubCiRunProvider.DownloadClientName,
            OurAirportsVerificationSource.HttpClientName,
            MledozeCountriesVerificationSource.HttpClientName,
        ];
    }
}
```

Then change `DependencyInjection.cs:171` to use `NamedHttpClients.AeroApi` in place of the `"AeroApi"` literal, so the constant is the single source of truth.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter DataSourceCatalogTests`
Expected: FAIL — `DataSourceCatalog` does not exist.

- [ ] **Step 4: Implement the catalog**

```csharp
using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using FlightAssistant.Infrastructure.ExternalServices.GitHub;
using FlightAssistant.Infrastructure.ExternalServices.Verification;

namespace FlightAssistant.Infrastructure.DataSources
{
    /// <summary>Catalog keys. Public because they are the status table's primary key and appear in URLs.</summary>
    public static class DataSourceKeys
    {
        public const string Postgres = "postgres";
        public const string AeroApi = "aeroapi";
        public const string AeroDataBox = "aerodatabox";
        public const string OurAirports = "ourairports";
        public const string Mledoze = "mledoze";
        public const string GitHub = "github";
        public const string Redis = "redis";
        public const string Apns = "apns";
        public const string AppInsights = "appinsights";
    }

    /// <summary>
    /// The nine outside dependencies, as the health page describes them. A hard-coded list rather
    /// than a scan: the "what is checked" sentence and the link are author-written prose that
    /// cannot be derived, and the completeness test is what keeps the list honest.
    /// </summary>
    public sealed class DataSourceCatalog : IDataSourceCatalog
    {
        private readonly IReadOnlyDictionary<string, DataSourceDescriptor> _byKey;

        public DataSourceCatalog()
        {
            Sources =
            [
                new(DataSourceKeys.Postgres, "PostgreSQL", DataSourceKind.Database,
                    DataSourceCriticality.Required,
                    "Opens a connection to the application database.",
                    ExternalUrl: null, HttpClientName: null, SupportsProbe: true, ProbeCostNote: null),

                new(DataSourceKeys.AeroApi, "FlightAware AeroAPI", DataSourceKind.FlightData,
                    DataSourceCriticality.Required,
                    "Signs in with the AeroAPI key and reads the account-usage report, which FlightAware bills at $0.00.",
                    "https://www.flightaware.com/aeroapi/portal/",
                    NamedHttpClients.AeroApi, SupportsProbe: true, ProbeCostNote: null),

                new(DataSourceKeys.AeroDataBox, "AeroDataBox (RapidAPI)", DataSourceKind.FlightData,
                    DataSourceCriticality.Optional,
                    "Checks that the AeroDataBox host answers. It does not verify the API key: every real AeroDataBox call is billed.",
                    "https://rapidapi.com/developer/dashboard",
                    AeroDataBoxFlightSearchProvider.HttpClientName, SupportsProbe: true,
                    ProbeCostNote: "Reachability only — every real AeroDataBox call is billed, so the key itself is not tested."),

                new(DataSourceKeys.OurAirports, "OurAirports", DataSourceKind.ReferenceData,
                    DataSourceCriticality.Required,
                    "Checks that the airports dataset is being served.",
                    "https://ourairports.com/data/",
                    OurAirportsVerificationSource.HttpClientName, SupportsProbe: true, ProbeCostNote: null),

                new(DataSourceKeys.Mledoze, "mledoze/countries", DataSourceKind.ReferenceData,
                    DataSourceCriticality.Required,
                    "Checks that the countries dataset is being served.",
                    "https://github.com/mledoze/countries",
                    MledozeCountriesVerificationSource.HttpClientName, SupportsProbe: true, ProbeCostNote: null),

                new(DataSourceKeys.GitHub, "GitHub API", DataSourceKind.Infrastructure,
                    DataSourceCriticality.Optional,
                    "Reads the rate-limit endpoint with the configured token, which GitHub does not meter.",
                    "https://github.com/salih1f/flight-assistant-backend/actions",
                    GitHubCiRunProvider.ApiClientName, SupportsProbe: true, ProbeCostNote: null),

                // The download client shares the GitHub row: it is the same host and the same
                // token. Listed so the completeness test sees it covered.
                new($"{DataSourceKeys.GitHub}-download", "GitHub artifact download", DataSourceKind.Infrastructure,
                    DataSourceCriticality.Optional,
                    "Downloads CI test-result archives. Covered by the GitHub API check above.",
                    ExternalUrl: null, GitHubCiRunProvider.DownloadClientName,
                    SupportsProbe: false, ProbeCostNote: null),

                new(DataSourceKeys.Redis, "Redis cache", DataSourceKind.Infrastructure,
                    DataSourceCriticality.Optional,
                    "Writes a short-lived probe key and reads it back.",
                    ExternalUrl: null, HttpClientName: null, SupportsProbe: true, ProbeCostNote: null),

                new(DataSourceKeys.Apns, "Apple Push Notification service", DataSourceKind.Notifications,
                    DataSourceCriticality.Required,
                    "Signs a provider token with the configured P8 key. No push is sent.",
                    "https://developer.apple.com/account/resources/authkeys/list",
                    HttpClientName: null, SupportsProbe: true, ProbeCostNote: null),

                new(DataSourceKeys.AppInsights, "Application Insights", DataSourceKind.Telemetry,
                    DataSourceCriticality.Optional,
                    "Checks only that a connection string resolves. Telemetry leaves through the SDK's own pipeline, which this process cannot observe.",
                    ExternalUrl: null, HttpClientName: null, SupportsProbe: true, ProbeCostNote: null),
            ];

            _byKey = Sources.ToDictionary(s => s.Key, StringComparer.Ordinal);
        }

        public IReadOnlyList<DataSourceDescriptor> Sources { get; }

        public DataSourceDescriptor? TryGet(string key) =>
            !string.IsNullOrWhiteSpace(key) && _byKey.TryGetValue(key, out var source) ? source : null;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter DataSourceCatalogTests`
Expected: PASS, 5 tests.

- [ ] **Step 6: Prove the completeness test bites**

Temporarily comment out the `mledoze` entry and re-run. Expected: `EveryNamedHttpClientHasACatalogEntry` fails and names `VerificationMledoze`. Restore it and re-run to green.

- [ ] **Step 7: Commit**

```bash
git add FlightAssistant.Infrastructure/DataSources FlightAssistant.Infrastructure.Tests/DataSources FlightAssistant.Infrastructure/DependencyInjection.cs
git commit -m "feat(health): catalog every outside data source (#460)"
```

---

### Task 4: The status entity, its EF configuration and migration

**Files:**
- Create: `FlightAssistant.Domain/DataSources/Entities/DataSourceStatus.cs`
- Create: `FlightAssistant.Infrastructure/Persistance/Configurations/DataSources/DataSourceStatusConfiguration.cs`
- Modify: `FlightAssistant.Infrastructure/Persistance/FlightAssistantDbContext.cs`
- Modify: `FlightAssistant.Infrastructure/Persistance/AdminAuditInterceptor.cs`
- Create: a migration under `FlightAssistant.Infrastructure/Persistance/Migrations/`

**Interfaces:**
- Consumes: `DataSourceOutcome` (Task 1).
- Produces: entity `DataSourceStatus` with settable properties `Key`, `LastOutcome` (string), `LastOutcomeUtc`, `LastOkUtc`, `LastError`, `ConsecutiveFailures`, `LastProbeUtc`, `LastProbeBy`; `DbSet<DataSourceStatus> DataSourceStatuses`.

- [ ] **Step 1: Create the entity**

```csharp
namespace FlightAssistant.Domain.DataSources.Entities
{
    /// <summary>
    /// The current observed state of one outside dependency — one row per catalog key.
    ///
    /// <para>
    /// Deliberately a flat, mutable row rather than a rich domain entity: it is diagnostics written
    /// from a buffer on a background flush, not a business invariant. <c>LastOutcome</c> is stored
    /// as its enum *name* so a psql session can read it, matching <c>verification_requests</c>.
    /// </para>
    /// </summary>
    public sealed class DataSourceStatus
    {
        public string Key { get; set; } = string.Empty;

        public string? LastOutcome { get; set; }

        public DateTime? LastOutcomeUtc { get; set; }

        /// <summary>Most recent Ok. Backs the Degraded/Unhealthy split.</summary>
        public DateTime? LastOkUtc { get; set; }

        /// <summary>Sanitized per the spec's §2.2 rule. Never an upstream exception's message.</summary>
        public string? LastError { get; set; }

        public int ConsecutiveFailures { get; set; }

        public DateTime? LastProbeUtc { get; set; }

        public string? LastProbeBy { get; set; }
    }
}
```

- [ ] **Step 2: Create the EF configuration**

```csharp
using FlightAssistant.Domain.DataSources.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.DataSources
{
    /// <summary>
    /// The <c>data_source_status</c> table: one row per catalog key, upserted by the buffered
    /// observer. snake_case columns, matching <c>verification_requests</c>.
    /// </summary>
    public sealed class DataSourceStatusConfiguration : IEntityTypeConfiguration<DataSourceStatus>
    {
        public void Configure(EntityTypeBuilder<DataSourceStatus> b)
        {
            b.ToTable("data_source_status");

            // The catalog key is the natural key. No surrogate id: there is exactly one row per
            // source and the key is what every read and write addresses it by.
            b.HasKey(x => x.Key);

            b.Property(x => x.Key).HasColumnName("key").HasMaxLength(64);
            b.Property(x => x.LastOutcome).HasColumnName("last_outcome").HasMaxLength(32);
            b.Property(x => x.LastOutcomeUtc).HasColumnName("last_outcome_utc");
            b.Property(x => x.LastOkUtc).HasColumnName("last_ok_utc");
            b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(1024);
            b.Property(x => x.ConsecutiveFailures).HasColumnName("consecutive_failures");
            b.Property(x => x.LastProbeUtc).HasColumnName("last_probe_utc");
            b.Property(x => x.LastProbeBy).HasColumnName("last_probe_by").HasMaxLength(256);

            // No index beyond the primary key: the table has one row per source — nine today — so
            // every read is a full scan of a handful of rows or a PK seek.
        }
    }
}
```

- [ ] **Step 3: Register the DbSet**

Add to `FlightAssistantDbContext`, beside the existing sets:

```csharp
public DbSet<DataSourceStatus> DataSourceStatuses => Set<DataSourceStatus>();
```

- [ ] **Step 4: Exclude the table from admin auditing**

In `AdminAuditInterceptor`, add `DataSourceStatus` to the set of entity types it never writes a row for, beside the existing `AdminAuditLog` and `VerificationRequest` entries, with this comment:

```csharp
// - DataSourceStatus (#460): written by the buffered health observer on a background flush, not
//   by an admin. Auditing it would put a row in the audit log for every outbound HTTP call the
//   application makes. The one operator action against this table — pressing "Check now" — writes
//   its own audit row explicitly in ProbeDataSourceCommandHandler, so the deliberate act is
//   audited while the automatic bookkeeping is not.
```

- [ ] **Step 5: Create the migration**

```bash
dotnet ef migrations add AddDataSourceStatus \
  --project FlightAssistant.Infrastructure \
  --startup-project FlightAssistant.API
```

Open the generated file and confirm it creates `data_source_status` with a primary key on `key` and no other index.

- [ ] **Step 6: Verify the migration applies**

Run: `dotnet ef database update --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API`
Expected: applies cleanly. Confirm with `\d data_source_status` in psql.

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test`
Expected: PASS — no existing test regresses. (`AdminAuditInterceptorTests` is the one to watch: the exclusion list changed.)

- [ ] **Step 8: Commit**

```bash
git add FlightAssistant.Domain/DataSources FlightAssistant.Infrastructure/Persistance
git commit -m "feat(health): add the data_source_status table (#460)"
```

---

### Task 5: The status repository

**Files:**
- Create: `FlightAssistant.Application/Interfaces/Repositories/IDataSourceStatusRepository.cs`
- Create: `FlightAssistant.Infrastructure/Persistance/Repositories/DataSourceStatusRepository.cs`
- Test: `FlightAssistant.Infrastructure.Tests/Persistance/Repositories/DataSourceStatusRepositoryPostgresTests.cs`

**Interfaces:**
- Consumes: `DataSourceStatus` (Task 4).
- Produces: `IDataSourceStatusRepository` with `Task<IReadOnlyList<DataSourceStatus>> GetAllAsync(CancellationToken)`, `Task<DataSourceStatus?> GetAsync(string key, CancellationToken)`, and `Task UpsertAsync(DataSourceStatus row, CancellationToken)`.

- [ ] **Step 1: Write the failing tests**

Follow the existing `VerificationRequestRepositoryPostgresTests` fixture pattern (`FlightAssistant.Infrastructure.Tests/Fixtures`) — a real Postgres, not InMemory, because this asserts upsert behaviour.

```csharp
[Fact]
public async Task UpsertAsync_InsertsARowThatDoesNotExist()
{
    var repo = new DataSourceStatusRepository(Db);

    await repo.UpsertAsync(new DataSourceStatus
    {
        Key = "aeroapi",
        LastOutcome = "Ok",
        LastOutcomeUtc = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc),
        LastOkUtc = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc),
    }, CancellationToken.None);

    var stored = await repo.GetAsync("aeroapi", CancellationToken.None);
    Assert.NotNull(stored);
    Assert.Equal("Ok", stored!.LastOutcome);
}

[Fact]
public async Task UpsertAsync_OverwritesARowThatAlreadyExists()
{
    var repo = new DataSourceStatusRepository(Db);
    var at = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    await repo.UpsertAsync(new DataSourceStatus { Key = "aeroapi", LastOutcome = "Ok", LastOutcomeUtc = at }, CancellationToken.None);
    await repo.UpsertAsync(new DataSourceStatus
    {
        Key = "aeroapi",
        LastOutcome = "Unauthorized",
        LastOutcomeUtc = at.AddMinutes(1),
        LastError = "aeroapi returned HTTP 401",
        ConsecutiveFailures = 1,
    }, CancellationToken.None);

    var stored = await repo.GetAsync("aeroapi", CancellationToken.None);
    Assert.Equal("Unauthorized", stored!.LastOutcome);
    Assert.Equal(1, stored.ConsecutiveFailures);

    var all = await repo.GetAllAsync(CancellationToken.None);
    Assert.Single(all.Where(r => r.Key == "aeroapi"));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter DataSourceStatusRepositoryPostgresTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement**

```csharp
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.DataSources.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    /// <summary>
    /// Reads and upserts <c>data_source_status</c>. The table holds one row per catalog key — nine
    /// today — so <see cref="GetAllAsync"/> reads the lot untracked rather than paging.
    /// </summary>
    public sealed class DataSourceStatusRepository : IDataSourceStatusRepository
    {
        private readonly FlightAssistantDbContext _db;

        public DataSourceStatusRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<IReadOnlyList<DataSourceStatus>> GetAllAsync(CancellationToken ct) =>
            await _db.DataSourceStatuses.AsNoTracking().ToListAsync(ct);

        public Task<DataSourceStatus?> GetAsync(string key, CancellationToken ct) =>
            _db.DataSourceStatuses.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, ct);

        public async Task UpsertAsync(DataSourceStatus row, CancellationToken ct)
        {
            var existing = await _db.DataSourceStatuses.FirstOrDefaultAsync(r => r.Key == row.Key, ct);

            if (existing is null)
            {
                _db.DataSourceStatuses.Add(row);
            }
            else
            {
                existing.LastOutcome = row.LastOutcome;
                existing.LastOutcomeUtc = row.LastOutcomeUtc;
                existing.LastOkUtc = row.LastOkUtc;
                existing.LastError = row.LastError;
                existing.ConsecutiveFailures = row.ConsecutiveFailures;
                // Probe fields are only written by the probe path, so an observation flush must
                // not blank them.
                if (row.LastProbeUtc is not null)
                {
                    existing.LastProbeUtc = row.LastProbeUtc;
                    existing.LastProbeBy = row.LastProbeBy;
                }
            }

            await _db.SaveChangesAsync(ct);
        }
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter DataSourceStatusRepositoryPostgresTests`
Expected: PASS.

- [ ] **Step 5: Register in DI**

In `DependencyInjection.AddInfrastructure`, beside the other repositories:

```csharp
services.AddScoped<IDataSourceStatusRepository, DataSourceStatusRepository>();
```

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Application/Interfaces/Repositories/IDataSourceStatusRepository.cs FlightAssistant.Infrastructure/Persistance/Repositories/DataSourceStatusRepository.cs FlightAssistant.Infrastructure.Tests/Persistance/Repositories FlightAssistant.Infrastructure/DependencyInjection.cs
git commit -m "feat(health): read and upsert data source status rows (#460)"
```

---

### Task 6: The buffered observer and its flush service

The write-throttling policy. This is the task most worth careful tests.

**Files:**
- Create: `FlightAssistant.Infrastructure/DataSources/BufferedDataSourceObserver.cs`
- Create: `FlightAssistant.API/HostedServices/DataSourceFlushService.cs`
- Test: `FlightAssistant.Infrastructure.Tests/DataSources/BufferedDataSourceObserverTests.cs`

**Interfaces:**
- Consumes: `IDataSourceObserver` (Task 1), `IDataSourceStatusRepository` (Task 5).
- Produces: `BufferedDataSourceObserver` (singleton, implements `IDataSourceObserver`) with `IReadOnlyCollection<DataSourceStatus> TakePending()` and `public static readonly TimeSpan CoalesceWindow`.

- [ ] **Step 1: Write the failing tests**

```csharp
using FlightAssistant.Application.DataSources;
using FlightAssistant.Infrastructure.DataSources;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FlightAssistant.Infrastructure.Tests.DataSources
{
    public class BufferedDataSourceObserverTests
    {
        private static readonly DateTimeOffset Start = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        private static (BufferedDataSourceObserver Observer, FakeTimeProvider Time) Build()
        {
            var time = new FakeTimeProvider(Start);
            return (new BufferedDataSourceObserver(time), time);
        }

        [Fact]
        public void AFirstObservation_IsPendingImmediately()
        {
            var (observer, _) = Build();

            observer.Record("aeroapi", DataSourceOutcome.Ok, Start);

            Assert.Single(observer.TakePending());
        }

        [Fact]
        public void TakePending_DrainsTheBuffer()
        {
            var (observer, _) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Ok, Start);

            observer.TakePending();

            Assert.Empty(observer.TakePending());
        }

        [Fact]
        public void RepeatedOkInsideTheWindow_Coalesces()
        {
            var (observer, time) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Ok, Start);
            observer.TakePending();

            time.Advance(TimeSpan.FromMinutes(1));
            observer.Record("aeroapi", DataSourceOutcome.Ok, time.GetUtcNow());

            Assert.Empty(observer.TakePending());
        }

        [Fact]
        public void RepeatedOkPastTheWindow_WritesAgain()
        {
            var (observer, time) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Ok, Start);
            observer.TakePending();

            time.Advance(BufferedDataSourceObserver.CoalesceWindow + TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Ok, time.GetUtcNow());

            Assert.Single(observer.TakePending());
        }

        [Fact]
        public void AFailureInsideTheWindow_IsNeverCoalesced()
        {
            var (observer, time) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Ok, Start);
            observer.TakePending();

            time.Advance(TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Failed, time.GetUtcNow(), "aeroapi returned HTTP 503");

            var pending = observer.TakePending();
            Assert.Single(pending);
            Assert.Equal("Failed", pending.Single().LastOutcome);
        }

        [Fact]
        public void AnOutcomeChangeInsideTheWindow_IsNeverCoalesced()
        {
            var (observer, time) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Failed, Start);
            observer.TakePending();

            time.Advance(TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Ok, time.GetUtcNow());

            Assert.Single(observer.TakePending());
        }

        [Fact]
        public void ConsecutiveFailures_CountUp_AndResetOnOk()
        {
            var (observer, time) = Build();

            observer.Record("aeroapi", DataSourceOutcome.Failed, Start);
            time.Advance(TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Failed, time.GetUtcNow());

            Assert.Equal(2, observer.TakePending().Single().ConsecutiveFailures);

            time.Advance(TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Ok, time.GetUtcNow());

            Assert.Equal(0, observer.TakePending().Single().ConsecutiveFailures);
        }

        [Fact]
        public void LastOkUtc_SurvivesALaterFailure()
        {
            var (observer, time) = Build();
            observer.Record("aeroapi", DataSourceOutcome.Ok, Start);
            observer.TakePending();

            time.Advance(TimeSpan.FromSeconds(1));
            observer.Record("aeroapi", DataSourceOutcome.Failed, time.GetUtcNow());

            Assert.Equal(Start.UtcDateTime, observer.TakePending().Single().LastOkUtc);
        }

        [Fact]
        public void Record_NeverThrows_OnAnUnknownKey()
        {
            var (observer, _) = Build();

            // Diagnostics must never fail the call they describe.
            observer.Record("", DataSourceOutcome.Ok, Start);
            observer.Record(null!, DataSourceOutcome.Ok, Start);
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter BufferedDataSourceObserverTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the observer**

```csharp
using System.Collections.Concurrent;
using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Domain.DataSources.Entities;

namespace FlightAssistant.Infrastructure.DataSources
{
    /// <summary>
    /// Holds the current state of every source in memory and hands the changed ones to
    /// <see cref="DataSourceFlushService"/> to persist.
    ///
    /// <para>
    /// <b>Why buffer at all.</b> Recording per call would mean one database write per outbound HTTP
    /// call the application makes. The policy instead writes immediately on anything an operator
    /// would want to see at once — a changed outcome, or any non-Ok outcome — and coalesces a run of
    /// identical Ok observations to at most one write per source per
    /// <see cref="CoalesceWindow"/>. A healthy, busy source therefore costs about twelve writes an
    /// hour, and a newly-failing one is visible on the next flush.
    /// </para>
    ///
    /// <para>
    /// <b>Never throws.</b> An observation is diagnostics. A failure to record one must not fail
    /// the call it was describing, so every entry point swallows bad input rather than validating it.
    /// </para>
    /// </summary>
    public sealed class BufferedDataSourceObserver : IDataSourceObserver
    {
        /// <summary>How long a run of identical Ok observations is collapsed into one write.</summary>
        public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMinutes(5);

        private readonly TimeProvider _timeProvider;
        private readonly ConcurrentDictionary<string, State> _byKey = new(StringComparer.Ordinal);

        public BufferedDataSourceObserver(TimeProvider timeProvider) => _timeProvider = timeProvider;

        private sealed class State
        {
            public DataSourceOutcome? LastOutcome;
            public DateTimeOffset LastOutcomeUtc;
            public DateTimeOffset? LastOkUtc;
            public string? LastError;
            public int ConsecutiveFailures;
            public DateTimeOffset? LastWrittenUtc;
            public bool Dirty;
        }

        public void Record(string key, DataSourceOutcome outcome, DateTimeOffset at, string? detail = null)
        {
            if (string.IsNullOrWhiteSpace(key)) return;

            var state = _byKey.GetOrAdd(key, _ => new State());

            lock (state)
            {
                var changed = state.LastOutcome != outcome;

                state.LastOutcome = outcome;
                state.LastOutcomeUtc = at;

                if (outcome == DataSourceOutcome.Ok)
                {
                    state.LastOkUtc = at;
                    state.ConsecutiveFailures = 0;
                    state.LastError = null;
                }
                else if (outcome is DataSourceOutcome.Failed or DataSourceOutcome.Unauthorized)
                {
                    state.ConsecutiveFailures++;
                    state.LastError = detail;
                }

                // Write at once on a change or on anything that is not Ok; otherwise only once the
                // coalescing window has elapsed since the last write.
                var mustWrite =
                    changed
                    || outcome != DataSourceOutcome.Ok
                    || state.LastWrittenUtc is null
                    || at - state.LastWrittenUtc >= CoalesceWindow;

                if (mustWrite) state.Dirty = true;
            }
        }

        /// <summary>
        /// The rows that need persisting, marking them clean. Called by the flush service and on
        /// graceful shutdown; a hard kill loses at most one coalescing window of "still fine",
        /// which is the only information this policy is willing to drop.
        /// </summary>
        public IReadOnlyCollection<DataSourceStatus> TakePending()
        {
            var pending = new List<DataSourceStatus>();
            var now = _timeProvider.GetUtcNow();

            foreach (var (key, state) in _byKey)
            {
                lock (state)
                {
                    if (!state.Dirty) continue;

                    pending.Add(new DataSourceStatus
                    {
                        Key = key,
                        LastOutcome = state.LastOutcome?.ToString(),
                        LastOutcomeUtc = state.LastOutcomeUtc.UtcDateTime,
                        LastOkUtc = state.LastOkUtc?.UtcDateTime,
                        LastError = state.LastError,
                        ConsecutiveFailures = state.ConsecutiveFailures,
                    });

                    state.Dirty = false;
                    state.LastWrittenUtc = now;
                }
            }

            return pending;
        }
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter BufferedDataSourceObserverTests`
Expected: PASS, 9 tests.

Note: `FakeTimeProvider` comes from `Microsoft.Extensions.TimeProvider.Testing`. If the test project does not already reference it, add it — check first, `AeroApiThrottleGateTests` uses a fake clock and may already pull it in.

- [ ] **Step 5: Prove the throttle tests bite**

Temporarily change `|| outcome != DataSourceOutcome.Ok` to `|| false` and re-run. Expected: `AFailureInsideTheWindow_IsNeverCoalesced` fails. Restore and re-run to green.

- [ ] **Step 6: Implement the flush service**

```csharp
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Infrastructure.DataSources;

namespace FlightAssistant.API.HostedServices
{
    /// <summary>
    /// Drains <see cref="BufferedDataSourceObserver"/> into <c>data_source_status</c> on a timer,
    /// and once more on graceful shutdown so a clean restart loses nothing.
    ///
    /// <para>
    /// The observer is a singleton and the repository is scoped, so each flush opens its own scope
    /// — the same shape the verification job uses. A flush that throws is logged and swallowed:
    /// losing a diagnostics write must never take the host down.
    /// </para>
    /// </summary>
    public sealed class DataSourceFlushService : BackgroundService
    {
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

        private readonly BufferedDataSourceObserver _observer;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DataSourceFlushService> _logger;

        public DataSourceFlushService(
            BufferedDataSourceObserver observer,
            IServiceScopeFactory scopeFactory,
            ILogger<DataSourceFlushService> logger)
        {
            _observer = observer;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(FlushInterval);

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                    await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutting down: fall through to the final flush below.
            }

            // CancellationToken.None deliberately — this flush runs *because* the host is stopping,
            // so it must not be cancelled by the token that signalled it.
            await FlushAsync(CancellationToken.None);
        }

        private async Task FlushAsync(CancellationToken ct)
        {
            try
            {
                var pending = _observer.TakePending();
                if (pending.Count == 0) return;

                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IDataSourceStatusRepository>();

                foreach (var row in pending)
                    await repo.UpsertAsync(row, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Failed to flush data source status rows.");
            }
        }
    }
}
```

- [ ] **Step 7: Register both in DI**

In `DependencyInjection.AddInfrastructure`:

```csharp
services.AddSingleton<IDataSourceCatalog, DataSourceCatalog>();
services.AddSingleton<BufferedDataSourceObserver>();
services.AddSingleton<IDataSourceObserver>(sp => sp.GetRequiredService<BufferedDataSourceObserver>());
```

The two-line singleton pattern matches how `AeroApiThrottleGate` is registered under `IAeroApiThrottleObserver` — the same instance under both names, never a second one.

In `Program.cs`, beside the other hosted services:

```csharp
builder.Services.AddHostedService<DataSourceFlushService>();
```

- [ ] **Step 8: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add FlightAssistant.Infrastructure/DataSources FlightAssistant.API/HostedServices/DataSourceFlushService.cs FlightAssistant.Infrastructure.Tests/DataSources FlightAssistant.Infrastructure/DependencyInjection.cs FlightAssistant.API/Program.cs
git commit -m "feat(health): buffer and flush data source observations (#460)"
```

---

### Task 7: The observing HTTP handler

**Files:**
- Create: `FlightAssistant.Infrastructure/DataSources/DataSourceObservingHandler.cs`
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs`
- Test: `FlightAssistant.Infrastructure.Tests/DataSources/DataSourceObservingHandlerTests.cs`

**Interfaces:**
- Consumes: `IDataSourceObserver` (Task 1), `NamedHttpClients` and `DataSourceKeys` (Task 3).
- Produces: `DataSourceObservingHandler` (transient) with a settable `string SourceKey`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData(200, DataSourceOutcome.Ok)]
[InlineData(204, DataSourceOutcome.Ok)]
[InlineData(401, DataSourceOutcome.Unauthorized)]
[InlineData(403, DataSourceOutcome.Unauthorized)]
[InlineData(429, DataSourceOutcome.RateLimited)]
[InlineData(500, DataSourceOutcome.Failed)]
[InlineData(503, DataSourceOutcome.Failed)]
public async Task MapsAStatusCodeToAnOutcome(int statusCode, DataSourceOutcome expected)
{
    var observer = new RecordingObserver();
    var client = BuildClient(observer, _ => new HttpResponseMessage((HttpStatusCode)statusCode));

    await client.GetAsync("/anything");

    Assert.Equal(expected, observer.Last!.Value.Outcome);
}

[Fact]
public async Task ATransportFailure_IsRecordedAsFailed_AndStillThrows()
{
    var observer = new RecordingObserver();
    var client = BuildClient(observer, _ => throw new HttpRequestException("connection refused"));

    await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/anything"));

    Assert.Equal(DataSourceOutcome.Failed, observer.Last!.Value.Outcome);
}

// The global constraint: an upstream message can name an internal address.
[Fact]
public async Task ATransportFailure_DoesNotLeakTheExceptionMessage()
{
    var observer = new RecordingObserver();
    var client = BuildClient(observer, _ => throw new HttpRequestException("connect failed to 10.0.0.7:5432"));

    await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/anything"));

    Assert.DoesNotContain("10.0.0.7", observer.Last!.Value.Detail ?? "");
}

[Fact]
public async Task AnObserverThatThrows_DoesNotBreakTheCall()
{
    var client = BuildClient(new ThrowingObserver(), _ => new HttpResponseMessage(HttpStatusCode.OK));

    var response = await client.GetAsync("/anything");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}
```

Write `RecordingObserver` and `ThrowingObserver` as small in-file fakes implementing `IDataSourceObserver`, and `BuildClient` as a helper wiring the handler over a stub `HttpMessageHandler` — follow the stub-handler pattern already used in `AeroApiFlightStatusProviderTests`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter DataSourceObservingHandlerTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement**

```csharp
using System.Net;
using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;

namespace FlightAssistant.Infrastructure.DataSources
{
    /// <summary>
    /// Records the outcome of every call through one named <see cref="HttpClient"/>.
    ///
    /// <para>
    /// A <see cref="DelegatingHandler"/> is the cheapest correct seam: it sees every call through
    /// the client, including calls in code not yet written, and it cannot be forgotten at a new
    /// call site the way an explicit <c>Record</c> can. That is the failure this whole feature
    /// exists to prevent.
    /// </para>
    ///
    /// <para>
    /// <b>It never changes the call it observes.</b> A transport failure is recorded and then
    /// rethrown unchanged; an observer that throws is swallowed. Diagnostics must not alter or
    /// break the thing being diagnosed.
    /// </para>
    /// </summary>
    public sealed class DataSourceObservingHandler : DelegatingHandler
    {
        private readonly IDataSourceObserver _observer;
        private readonly TimeProvider _timeProvider;

        public DataSourceObservingHandler(IDataSourceObserver observer, TimeProvider timeProvider)
        {
            _observer = observer;
            _timeProvider = timeProvider;
        }

        /// <summary>Set by the DI registration, one instance per named client.</summary>
        public string SourceKey { get; set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                var response = await base.SendAsync(request, ct);
                Record(Map(response.StatusCode), Describe(response.StatusCode));
                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // The category only. An upstream message can name an internal address — see the
                // same rule on VerificationSourceDownloader.
                Record(DataSourceOutcome.Failed, $"{SourceKey} could not be reached (network error)");
                throw;
            }
        }

        private static DataSourceOutcome Map(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DataSourceOutcome.Unauthorized,
            HttpStatusCode.TooManyRequests => DataSourceOutcome.RateLimited,
            _ when (int)status >= 200 && (int)status < 400 => DataSourceOutcome.Ok,
            _ => DataSourceOutcome.Failed,
        };

        // Null on success: there is nothing to explain, and a stored "detail" on a healthy row
        // would be noise the panel has to filter out.
        private string? Describe(HttpStatusCode status) =>
            Map(status) == DataSourceOutcome.Ok ? null : $"{SourceKey} returned HTTP {(int)status}";

        private void Record(DataSourceOutcome outcome, string? detail)
        {
            try
            {
                _observer.Record(SourceKey, outcome, _timeProvider.GetUtcNow(), detail);
            }
            catch
            {
                // Never break the call being observed.
            }
        }
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter DataSourceObservingHandlerTests`
Expected: PASS, 11 tests.

- [ ] **Step 5: Wire it onto all six named clients**

In `DependencyInjection.AddInfrastructure`, add a local helper and apply it to each `AddHttpClient` builder:

```csharp
// One handler instance per client, each carrying its own catalog key. Added LAST on each
// builder so it is the innermost handler: it then observes the actual transport result of each
// attempt, not the resilience pipeline's verdict after retries.
static IHttpClientBuilder Observe(IHttpClientBuilder builder, string sourceKey) =>
    builder.AddHttpMessageHandler(sp => new DataSourceObservingHandler(
        sp.GetRequiredService<IDataSourceObserver>(),
        sp.GetRequiredService<TimeProvider>())
    { SourceKey = sourceKey });
```

Apply to: the AeroAPI builder (`DataSourceKeys.AeroApi`), AeroDataBox (`DataSourceKeys.AeroDataBox`), the GitHub API client (`DataSourceKeys.GitHub`), the GitHub download client (`$"{DataSourceKeys.GitHub}-download"`), OurAirports (`DataSourceKeys.OurAirports`), mledoze (`DataSourceKeys.Mledoze`).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS. Watch `AeroApiDependencyInjectionTests` — it asserts the AeroAPI client's configuration and may need the new handler accounted for.

- [ ] **Step 7: Commit**

```bash
git add FlightAssistant.Infrastructure/DataSources/DataSourceObservingHandler.cs FlightAssistant.Infrastructure.Tests/DataSources/DataSourceObservingHandlerTests.cs FlightAssistant.Infrastructure/DependencyInjection.cs
git commit -m "feat(health): observe every outbound call to a catalogued source (#460)"
```

---

### Task 8: Non-HTTP seams and the startup seeder

**Files:**
- Modify: `FlightAssistant.Infrastructure/HealthChecks/DatabaseHealthCheck.cs`
- Modify: `FlightAssistant.Infrastructure/Caching/RedisCacheService.cs`
- Create: `FlightAssistant.API/HostedServices/DataSourceSeeder.cs`
- Test: `FlightAssistant.API.Tests/HostedServices/DataSourceSeederTests.cs`

**Interfaces:**
- Consumes: `IDataSourceObserver`, `IDataSourceCatalog`, `DataSourceKeys`.
- Produces: `DataSourceSeeder` (an `IHostedService`).

- [ ] **Step 1: Record from `DatabaseHealthCheck`**

Inject `IDataSourceObserver` and `TimeProvider`; record `Ok` when `CanConnectAsync` returns true, `Failed` with the detail `"The database could not be reached"` when it returns false or throws. Keep the existing `HealthCheckResult` returns exactly as they are — this adds an observation, it does not change the check's answer.

- [ ] **Step 2: Record from `RedisCacheService`**

Wrap the existing `IDistributedCache` get and set: `Ok` on a completed operation, `Failed` with `"The Redis cache could not be reached"` on an exception, then rethrow. Redis is a cache — the existing behaviour on failure must not change.

- [ ] **Step 3: Write the failing seeder test**

```csharp
[Fact]
public async Task SeedsNotConfigured_ForAnOptionalSourceWithNoCredentials()
{
    var observer = new RecordingObserver();
    var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build(); // nothing configured
    var seeder = new DataSourceSeeder(new DataSourceCatalog(), observer, config, TimeProvider.System);

    await seeder.StartAsync(CancellationToken.None);

    Assert.Equal(DataSourceOutcome.NotConfigured, observer.For(DataSourceKeys.GitHub)!.Value.Outcome);
    Assert.Equal(DataSourceOutcome.NotConfigured, observer.For(DataSourceKeys.AeroDataBox)!.Value.Outcome);
    Assert.Equal(DataSourceOutcome.NotConfigured, observer.For(DataSourceKeys.Redis)!.Value.Outcome);
}

// Without this, a required source would read NotConfigured rather than Unknown before any traffic —
// a much weaker claim than "we have not looked yet".
[Fact]
public async Task DoesNotSeed_ARequiredSource()
{
    var observer = new RecordingObserver();
    var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var seeder = new DataSourceSeeder(new DataSourceCatalog(), observer, config, TimeProvider.System);

    await seeder.StartAsync(CancellationToken.None);

    Assert.Null(observer.For(DataSourceKeys.AeroApi));
    Assert.Null(observer.For(DataSourceKeys.Postgres));
}

[Fact]
public async Task DoesNotSeed_AnOptionalSourceThatIsConfigured()
{
    var observer = new RecordingObserver();
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection([new("GitHub:Token", "ghp_example")])
        .Build();
    var seeder = new DataSourceSeeder(new DataSourceCatalog(), observer, config, TimeProvider.System);

    await seeder.StartAsync(CancellationToken.None);

    Assert.Null(observer.For(DataSourceKeys.GitHub));
}
```

- [ ] **Step 4: Run to verify they fail**

Run: `dotnet test FlightAssistant.API.Tests --filter DataSourceSeederTests`
Expected: FAIL — type does not exist.

- [ ] **Step 5: Implement the seeder**

An `IHostedService` whose `StartAsync` records `NotConfigured` for each `Optional` source whose credentials are absent — `GitHub:Token`, the AeroDataBox `ApiKey`, the Redis connection string, and `ApplicationInsightsConnection.Resolve(configuration)`. It records nothing for a `Required` source and nothing for a configured optional one, with this comment:

```csharp
// Required sources are deliberately not seeded. They are called in the normal course of the
// application's work, so their first real observation arrives on its own — and until it does,
// Unknown ("we have not looked yet") is the honest answer. Seeding them would claim something
// stronger than the process knows.
```

- [ ] **Step 6: Run to verify they pass, then register**

Run: `dotnet test FlightAssistant.API.Tests --filter DataSourceSeederTests`
Expected: PASS, 3 tests.

Register in `Program.cs`: `builder.Services.AddHostedService<DataSourceSeeder>();`

- [ ] **Step 7: Commit**

```bash
git add FlightAssistant.Infrastructure/HealthChecks FlightAssistant.Infrastructure/Caching FlightAssistant.API/HostedServices/DataSourceSeeder.cs FlightAssistant.API.Tests/HostedServices FlightAssistant.API/Program.cs
git commit -m "feat(health): observe database, Redis and unconfigured sources (#460)"
```

---

### Task 9: `GET /admin/health/sources`

**Files:**
- Create: `FlightAssistant.Contracts/Responses/Admin/AdminDataSourceDto.cs`
- Create: `FlightAssistant.Application/Queries/Admin/DataSources/GetDataSourcesQuery.cs`
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminHealthEndpoints.cs`
- Test: `FlightAssistant.Application.Tests/Queries/Admin/DataSources/GetDataSourcesQueryTests.cs`
- Test: `FlightAssistant.API.Tests/Integration/AdminDataSourcesEndpointTests.cs`

**Interfaces:**
- Consumes: `IDataSourceCatalog`, `IDataSourceStatusRepository`, `DataSourceStatusEvaluator`.
- Produces: `AdminDataSourceDto` — the exact field list the admin panel binds to in Phase 2.

- [ ] **Step 1: Define the DTO**

```csharp
namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <param name="Status">Derived by the API, not by the caller: the Degraded/Unhealthy line is
    /// drawn in one place (<c>DataSourceStatusEvaluator</c>) so the panel renders rather than
    /// re-derives.</param>
    /// <param name="WhatIsChecked">Rendered verbatim. The answer to "green — but green for what?"</param>
    /// <param name="LastError">Sanitized; exception-derived detail is non-Production only.</param>
    public sealed record AdminDataSourceDto(
        string Key,
        string DisplayName,
        string Kind,
        string Criticality,
        string Status,
        string WhatIsChecked,
        string? ExternalUrl,
        string? LastOutcome,
        DateTime? LastOutcomeUtc,
        DateTime? LastOkUtc,
        string? LastError,
        int ConsecutiveFailures,
        bool CanProbe,
        string? ProbeCostNote,
        DateTime? LastProbeUtc,
        string? LastProbeBy);
}
```

- [ ] **Step 2: Write the failing query tests**

```csharp
[Fact]
public async Task ReturnsOneRowPerCatalogSource_EvenWhenNeverObserved()
{
    var handler = BuildHandler(storedRows: []);

    var result = await handler.Handle(new GetDataSourcesQuery(), CancellationToken.None);

    Assert.Equal(new DataSourceCatalog().Sources.Count, result.Count);
    Assert.All(result, r => Assert.Equal(nameof(DataSourceStatusValue.Unknown), r.Status));
}

[Fact]
public async Task AnObservedSource_CarriesItsDerivedStatus()
{
    var handler = BuildHandler(storedRows:
    [
        new() { Key = DataSourceKeys.AeroApi, LastOutcome = "Unauthorized", LastOutcomeUtc = Now },
    ]);

    var result = await handler.Handle(new GetDataSourcesQuery(), CancellationToken.None);

    var aeroApi = result.Single(r => r.Key == DataSourceKeys.AeroApi);
    Assert.Equal(nameof(DataSourceStatusValue.Unhealthy), aeroApi.Status);
}

[Fact]
public async Task AStoredRowWithNoCatalogEntry_IsIgnored()
{
    var handler = BuildHandler(storedRows: [new() { Key = "removed-source", LastOutcome = "Ok" }]);

    var result = await handler.Handle(new GetDataSourcesQuery(), CancellationToken.None);

    Assert.DoesNotContain(result, r => r.Key == "removed-source");
}

[Fact]
public async Task LastError_IsOmitted_InProduction()
{
    var handler = BuildHandler(
        storedRows: [new() { Key = DataSourceKeys.AeroApi, LastOutcome = "Failed", LastError = "aeroapi returned HTTP 503" }],
        environmentName: "Production");

    var result = await handler.Handle(new GetDataSourcesQuery(), CancellationToken.None);

    Assert.Null(result.Single(r => r.Key == DataSourceKeys.AeroApi).LastError);
}
```

- [ ] **Step 3: Run to verify they fail, then implement**

Run: `dotnet test FlightAssistant.Application.Tests --filter GetDataSourcesQueryTests`
Expected: FAIL.

Implement the handler: read the catalog, read all stored rows into a dictionary by key, left-join catalog → rows, derive each status via `DataSourceStatusEvaluator.Evaluate`, and project to the DTO. Sources are returned in catalog order, grouped by `Kind` on the panel side.

- [ ] **Step 4: Map the endpoint**

On the existing `/admin/health` group in `AdminHealthEndpoints`:

```csharp
// GET /admin/health/sources
group.MapGet("sources", async (IMediator mediator, CancellationToken ct) =>
    Results.Ok(await mediator.Send(new GetDataSourcesQuery(), ct)))
.WithName("AdminGetDataSources")
.WithSummary("Every outside data source and its observed status")
.WithDescription(
    "One row per catalogued dependency: its current status, when it was last seen working, when " +
    "it was last observed at all, a sentence saying what that status is based on, and where to " +
    "check it yourself. Reads a small table and runs no checks — unlike GET /admin/health, which " +
    "runs every registered health check live. A source that has never been observed is reported " +
    "as Unknown, never as healthy.")
.Produces<List<AdminDataSourceDto>>(200);
```

- [ ] **Step 5: Write the integration test**

Follow `AdminHealthEndpointTests`: assert 200, one row per catalog source, that an unauthenticated caller gets 401, and that every row carries a non-empty `whatIsChecked`.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add FlightAssistant.Contracts/Responses/Admin/AdminDataSourceDto.cs FlightAssistant.Application/Queries/Admin/DataSources FlightAssistant.API/Endpoints/Admin/AdminHealthEndpoints.cs FlightAssistant.Application.Tests/Queries/Admin/DataSources FlightAssistant.API.Tests/Integration/AdminDataSourcesEndpointTests.cs
git commit -m "feat(health): serve the data source inventory (#460)"
```

---

### Task 10: The probe endpoint

**Files:**
- Create: `FlightAssistant.Application/Interfaces/DataSources/IDataSourceProbe.cs`
- Create: `FlightAssistant.Infrastructure/DataSources/Probes/` (one probe per source)
- Create: `FlightAssistant.Application/Commands/Admin/DataSources/ProbeDataSourceCommand.cs`
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminHealthEndpoints.cs`
- Modify: `FlightAssistant.API/Program.cs` (rate-limit policy)
- Test: `FlightAssistant.API.Tests/Integration/AdminDataSourceProbeEndpointTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: `IDataSourceProbe { string Key { get; } Task<DataSourceOutcome> ProbeAsync(CancellationToken ct); }`.

- [ ] **Step 1: Define the probe interface and implement one per source**

Per the spec's §5.1 table. Each probe records through `IDataSourceObserver` and returns its outcome:

| Key | Implementation |
|---|---|
| `postgres` | `db.Database.CanConnectAsync(ct)` |
| `aeroapi` | `GET account/usage` on the `AeroApi` client — free, and the real credential check |
| `github` | `GET rate_limit` on the GitHub API client |
| `ourairports` | `HEAD` `OurAirportsVerificationSource.DatasetUrl` |
| `mledoze` | `HEAD` `MledozeCountriesVerificationSource.DatasetUrl` |
| `redis` | set `health:probe` with a 10s TTL, read it back, compare |
| `apns` | sign a provider token with the configured P8 key; send nothing |
| `aerodatabox` | `HEAD` the API base — reachability only, per its `ProbeCostNote` |
| `appinsights` | `ApplicationInsightsConnection.Resolve(configuration) is not null` |

- [ ] **Step 2: Write the failing integration tests**

```csharp
[Fact]
public async Task Probe_RequiresAdmin()
{
    var response = await AnonymousClient.PostAsync("/admin/health/sources/aeroapi/probe", null);
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
}

[Fact]
public async Task Probe_ReturnsTheRefreshedRow()
{
    var response = await AdminClient.PostAsync("/admin/health/sources/postgres/probe", null);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var row = await response.Content.ReadFromJsonAsync<AdminDataSourceDto>();
    Assert.Equal("postgres", row!.Key);
    Assert.NotNull(row.LastProbeUtc);
}

[Fact]
public async Task Probe_RejectsAnUnknownSource()
{
    var response = await AdminClient.PostAsync("/admin/health/sources/not-a-source/probe", null);
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}

// A source whose probe would cost money must refuse rather than quietly spend it.
[Fact]
public async Task Probe_RejectsASourceThatDoesNotSupportOne()
{
    var response = await AdminClient.PostAsync("/admin/health/sources/github-download/probe", null);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
}

[Fact]
public async Task Probe_WritesAnAuditRow()
{
    await AdminClient.PostAsync("/admin/health/sources/postgres/probe", null);

    var audit = await Db.AdminAuditLogs.OrderByDescending(a => a.Id).FirstAsync();
    Assert.Contains("postgres", audit.Summary);
}

[Fact]
public async Task Probe_RefusesASecondCallInsideTheRateLimitWindow()
{
    await AdminClient.PostAsync("/admin/health/sources/postgres/probe", null);
    var second = await AdminClient.PostAsync("/admin/health/sources/postgres/probe", null);

    Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
}
```

- [ ] **Step 3: Run to verify they fail, then implement the command handler**

The handler: resolve the descriptor (404 if unknown), refuse if `!SupportsProbe` (400, message = `ProbeCostNote` when present), run the matching `IDataSourceProbe`, write the probe fields and an explicit `AdminAuditLog` row, then return the refreshed DTO by reusing `GetDataSourcesQuery`'s projection.

- [ ] **Step 4: Add the rate-limit policy**

In `Program.cs`, beside the existing `flight-search` and `loyalty-programs` policies, a `health-probe` fixed-window policy partitioned by `{admin id}:{source key}` — one permit per 30 seconds, queue limit 0. Apply with `.RequireRateLimiting("health-probe")` on the endpoint.

- [ ] **Step 5: Map the endpoint**

```csharp
// POST /admin/health/sources/{key}/probe
group.MapPost("sources/{key}/probe", async (string key, IMediator mediator, CancellationToken ct) =>
    Results.Ok(await mediator.Send(new ProbeDataSourceCommand(key), ct)))
.WithName("AdminProbeDataSource")
.WithSummary("Check one data source now")
.WithDescription(
    "Runs this source's check immediately and returns its refreshed row. Verifies credentials " +
    "where that is free and reachability where it is not — each source's 'whatIsChecked' says " +
    "which. Rate limited to one probe per source every 30 seconds, and audited. 400 when the " +
    "source has no probe, 404 when the key is not catalogued.")
.RequireRateLimiting("health-probe")
.Produces<AdminDataSourceDto>(200)
.ProducesProblem(400)
.ProducesProblem(404)
.ProducesProblem(429);
```

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 7: Verify the AeroAPI probe against the real API**

Run the API locally with a real `AeroApi:ApiKey` and press the probe. Confirm in the logs that it calls `account/usage` and records `Ok`. Then blank the key and confirm it records `Unauthorized` and the row reads Unhealthy — **this is the behaviour the whole issue exists to fix**, so verify it rather than assuming it.

- [ ] **Step 8: Commit and open the PR**

```bash
git add FlightAssistant.Application/Interfaces/DataSources FlightAssistant.Infrastructure/DataSources/Probes FlightAssistant.Application/Commands/Admin/DataSources FlightAssistant.API
git commit -m "feat(health): check one data source on demand (#460)"
git push -u origin feature/460-data-source-inventory
gh pr create --base development --title "feat(health): a data-source inventory for the system health page (#460)"
```

---

# Phase 2 — Admin panel (PR 2)

**Do not start Phase 2 until Phase 1 has merged** and the OpenAPI client has been regenerated — the panel cannot compile against types that do not exist yet.

Phase 2 runs in the **`flight-assistant-admin-panel` repository**, not this one. Its issue does not
exist yet: open one there when Phase 1 merges, and substitute its number for `#<panel issue>` in the
commit and branch names below. That is the only deliberately unfilled value in this plan.

### Task 11: Regenerate the client and add the `/health` route

**Files:**
- Modify: `openapi/openapi.json` (regenerated)
- Create: `src/pages/health/SystemHealthPage.tsx`
- Create: `src/pages/health/HealthPrimitives.tsx`
- Modify: `src/pages/dashboard/SystemHealthSection.tsx`
- Modify: `src/router.tsx`, `src/components/layout/navigation.ts`

- [ ] **Step 1: Regenerate**

```bash
cp ../flight-assistant-backend/spec/openapi.json openapi/openapi.json
npm run generate:api
```

Confirm `src/api/generated/model/adminDataSourceDto.ts` appears with the fields from Task 9.

- [ ] **Step 2: Lift the shared primitives**

Move `Status`, `Panel` and `statusColor` out of `SystemHealthSection.tsx` into `src/pages/health/HealthPrimitives.tsx` and import them from both places. Extend `statusColor` with the two new values:

```tsx
// The API's DataSourceStatusValue names, alongside the HealthStatus names already here.
const statusColor: Record<string, string> = {
  Healthy: 'text-green-600',
  Ok: 'text-green-600',
  Degraded: 'text-amber-600',
  Unhealthy: 'text-red-600',
  // Gray, not amber: "not configured" is a deliberate choice and "unknown" is an absence of
  // information. Neither is a fault, and neither may read as healthy.
  NotConfigured: 'text-gray-500',
  Unknown: 'text-gray-500',
}
```

- [ ] **Step 3: Create the page and move the existing panels**

`SystemHealthPage` renders the existing check table and the Migrations / Flight sync / Verification panels, moved from `SystemHealthSection` unchanged, plus the `DataSourcesSection` from Task 12.

- [ ] **Step 4: Add the route and nav entry**

`{ path: '/health', element: <SystemHealthPage /> }` in `router.tsx`, and a System group entry in `navigation.ts`.

- [ ] **Step 5: Run the tests**

Run: `npm test`
Expected: PASS. `SystemHealth.test.tsx` moves with the component — update its import path.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(health): give system health its own page (#<panel issue>)"
```

---

### Task 12: The data sources table

**Files:**
- Create: `src/pages/health/DataSourcesSection.tsx`
- Test: `src/pages/health/DataSourcesSection.test.tsx`

- [ ] **Step 1: Write the failing tests**

```tsx
it('renders an unobserved source as unknown, not healthy', () => {
  renderWith([source({ key: 'aeroapi', status: 'Unknown' })])

  expect(screen.getByText('Unknown')).toHaveClass('text-gray-500')
  expect(screen.queryByText('Healthy')).not.toBeInTheDocument()
})

it('explains an unobserved source rather than alarming about it', () => {
  renderWith([source({ key: 'aeroapi', status: 'Unknown' })])

  expect(screen.getByText(/not observed since deploy/i)).toBeInTheDocument()
})

it('shows why a source is degraded', () => {
  renderWith([source({
    key: 'aeroapi', status: 'Degraded',
    lastOutcome: 'Unauthorized', lastError: 'aeroapi returned HTTP 401',
  })])

  expect(screen.getByText(/HTTP 401/)).toBeInTheDocument()
})

it('renders an unconfigured optional source in gray, never red', () => {
  renderWith([source({ key: 'github', status: 'NotConfigured', criticality: 'Optional' })])

  expect(screen.getByText('NotConfigured')).toHaveClass('text-gray-500')
})

it('renders what each source actually checks', () => {
  renderWith([source({ key: 'aeroapi', whatIsChecked: 'Signs in with the AeroAPI key…' })])

  expect(screen.getByText(/Signs in with the AeroAPI key/)).toBeInTheDocument()
})

it('links out to the source’s own dashboard', () => {
  renderWith([source({ key: 'aeroapi', externalUrl: 'https://www.flightaware.com/aeroapi/portal/' })])

  const link = screen.getByRole('link', { name: /FlightAware AeroAPI/ })
  expect(link).toHaveAttribute('href', 'https://www.flightaware.com/aeroapi/portal/')
  expect(link).toHaveAttribute('rel', expect.stringContaining('noopener'))
})

it('groups rows by kind', () => {
  renderWith([
    source({ key: 'postgres', kind: 'Database' }),
    source({ key: 'aeroapi', kind: 'FlightData' }),
  ])

  expect(screen.getByRole('rowgroup', { name: /Database/i })).toBeInTheDocument()
})
```

- [ ] **Step 2: Run to verify they fail**

Run: `npm test -- DataSourcesSection`
Expected: FAIL — component does not exist.

- [ ] **Step 3: Implement, then run to verify they pass**

Columns: name (linked when `externalUrl` is set) · status · last OK · last checked · what's checked · Check now. Grouped by `kind`. Timestamps through the existing `formatUtcMinute`; errors through the existing red `text-xs` treatment. Load errors through `apiErrorMessage`, matching the sections beside it.

- [ ] **Step 4: Commit**

```bash
git add src/pages/health && git commit -m "feat(health): list every data source and what it checks (#<panel issue>)"
```

---

### Task 13: "Check now"

**Files:**
- Modify: `src/pages/health/DataSourcesSection.tsx`
- Test: `src/pages/health/DataSourcesSection.test.tsx`

- [ ] **Step 1: Write the failing tests**

```tsx
it('warns before a probe that costs money', async () => {
  renderWith([source({
    key: 'aerodatabox', canProbe: true,
    probeCostNote: 'Reachability only — every real AeroDataBox call is billed…',
  })])

  await userEvent.click(screen.getByRole('button', { name: /check now/i }))

  expect(screen.getByText(/every real AeroDataBox call is billed/i)).toBeInTheDocument()
})

it('offers no button for a source that cannot be probed', () => {
  renderWith([source({ key: 'github-download', canProbe: false })])

  expect(screen.queryByRole('button', { name: /check now/i })).not.toBeInTheDocument()
})

it('reports a rate-limited probe without claiming the source failed', async () => {
  server.use(http.post('*/admin/health/sources/postgres/probe', () => new HttpResponse(null, { status: 429 })))
  renderWith([source({ key: 'postgres', canProbe: true })])

  await userEvent.click(screen.getByRole('button', { name: /check now/i }))

  expect(await screen.findByText(/checked too recently/i)).toBeInTheDocument()
  expect(screen.queryByText('Unhealthy')).not.toBeInTheDocument()
})
```

The third test matters: a 429 is our own rate limit, not the source failing. Rendering it as a fault would be a new version of the bug this feature exists to fix.

- [ ] **Step 2: Run to verify they fail, implement, run to verify they pass**

Run: `npm test -- DataSourcesSection`

- [ ] **Step 3: Commit**

```bash
git add src/pages/health && git commit -m "feat(health): check one data source on demand (#<panel issue>)"
```

---

### Task 14: Shrink the dashboard section

**Files:**
- Modify: `src/pages/dashboard/SystemHealthSection.tsx`
- Test: `src/pages/SystemHealth.test.tsx`

- [ ] **Step 1: Write the failing tests**

```tsx
it('summarises health and links to the page', () => {
  renderDashboardSection({ status: 'Healthy', sources: 9, notOk: 0 })

  expect(screen.getByRole('link', { name: /system health/i })).toHaveAttribute('href', '/health')
  expect(screen.getByText(/9 sources/i)).toBeInTheDocument()
})

it('names the trouble when something is not ok', () => {
  renderDashboardSection({ status: 'Degraded', sources: 9, notOk: 2 })

  expect(screen.getByText(/2 need attention/i)).toBeInTheDocument()
})
```

- [ ] **Step 2: Run to verify they fail, implement, run to verify they pass**

Replace the section body with the summary line. Keep the existing `useAdminGetHealth` call for overall status and add `useAdminGetDataSources` for the counts.

- [ ] **Step 3: Run the full suite, lint, typecheck**

```bash
npm test && npm run lint && npx tsc --noEmit
```
Expected: all PASS.

- [ ] **Step 4: Commit and open the panel PR**

```bash
git add -A
git commit -m "feat(health): summarise system health on the dashboard (#<panel issue>)"
git push -u origin feature/<n>-data-source-inventory
gh pr create --base main --title "feat(health): a data-source inventory page (#<panel issue>)"
```

---

## Verification checklist

Before marking Phase 1 done:

- [ ] `dotnet test` passes in full
- [ ] `dotnet ef database update` applies on a clean database
- [ ] With a valid AeroAPI key, the `aeroapi` probe records `Ok`
- [ ] With a blank AeroAPI key, the `aeroapi` probe records `Unauthorized` and the row reads **Unhealthy** — the original bug, verified fixed rather than assumed
- [ ] Removing a catalog entry fails `EveryNamedHttpClientHasACatalogEntry`
- [ ] A restart leaves rows intact (they are persisted, not in-memory)
- [ ] The audit log has one row per probe and **no** rows from ordinary observation flushes
