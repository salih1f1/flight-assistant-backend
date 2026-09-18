# Job Switches Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an admin stop and start the flight-status sync and the reference verification job from the admin panel, without a deploy.

**Architecture:** Config keeps its current meaning — `Enabled=false` still terminates the hosted service and nothing reachable from the panel can lift it. Inside that ceiling, a persisted per-job switch is consulted on every loop tick, so a running loop can decide to do nothing. Each heartbeat gains a `SwitchedOff` state so `/admin/health` can tell an operator's stop from a fault.

**Tech Stack:** .NET 10, EF Core (Npgsql), MediatR, xUnit + Moq/NSubstitute, `Microsoft.Extensions.TimeProvider.Testing` for fake clocks, minimal APIs. Admin panel: React 19, TypeScript, Vitest, orval-generated client.

**Spec:** `docs/superpowers/specs/2026-09-18-job-switches-design.md`

## Global Constraints

- **Config is a hard ceiling.** `effective = configEnabled && switchEnabled`. The config branch still `return`s from `ExecuteAsync` and terminates the service — do not turn it into a tick-time check.
- **A switched-off loop must make no AeroAPI call and do no work.** This is the failure mode with no visible symptom; every loop test asserts it directly.
- **`SwitchedOff` is a new heartbeat outcome, distinct from `VerificationJobOutcome.Paused`.** `Paused` means the cycle ran and held work back because the AeroAPI budget is exhausted. Never reuse it for an operator stop.
- **Manual triggers never consult the switch.** `POST /admin/flights/{id}/sync` and the manual verification endpoints keep working while a loop is off.
- Column names are snake_case, matching `verification_requests` and `data_source_status`.
- Enum-like columns store enum *names*, not ints.
- Nullable reference types enabled. XML doc comments explain *why*, not *what* — house style is `FlightAssistant.Application/Interfaces/Verification/IVerificationSource.cs`. Document public members, not only types.
- Tests are xUnit: `[Fact]` / `[Theory]`, `Assert.Equal(expected, actual)`.
- This is two booleans with a config ceiling, **not** a feature-flag framework. No percentage rollouts, no per-user targeting, no flags for anything but these two jobs.

---

# Phase 1 — Backend (PR 1)

### Task 1: Job keys and the switch contract

**Files:**
- Create: `FlightAssistant.Application/Jobs/JobNames.cs`
- Create: `FlightAssistant.Application/Jobs/JobSwitchState.cs`
- Create: `FlightAssistant.Application/Interfaces/Jobs/IJobSwitch.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `JobNames.FlightSync` (`"flight-sync"`), `JobNames.Verification` (`"verification"`), `JobNames.All`; record `JobSwitchState`; `IJobSwitch.IsEnabledAsync(string job, CancellationToken) → Task<bool>` and `IJobSwitch.GetAsync(string job, CancellationToken) → Task<JobSwitchState>`.

- [ ] **Step 1: Create the job keys**

```csharp
namespace FlightAssistant.Application.Jobs
{
    /// <summary>
    /// The jobs an admin can switch off. Deliberately a closed list rather than an open registry:
    /// this is two booleans with a config ceiling, not a feature-flag framework, and a closed list
    /// is what keeps it from growing into one.
    /// </summary>
    public static class JobNames
    {
        public const string FlightSync = "flight-sync";
        public const string Verification = "verification";

        public static readonly IReadOnlyList<string> All = [FlightSync, Verification];
    }
}
```

- [ ] **Step 2: Create the state record and interface**

```csharp
namespace FlightAssistant.Application.Jobs
{
    /// <param name="Enabled">The runtime switch alone. It never encodes the config ceiling —
    /// callers combine the two, so neither value can be mistaken for the other.</param>
    public sealed record JobSwitchState(
        string Job,
        bool Enabled,
        DateTime? ChangedUtc,
        string? ChangedBy,
        string? Reason);
}
```

```csharp
using FlightAssistant.Application.Jobs;

namespace FlightAssistant.Application.Interfaces.Jobs
{
    /// <summary>
    /// The runtime on/off switch for a background job, read on every loop tick.
    ///
    /// <para>
    /// Answers the switch only, never the config ceiling: <c>FlightSync:Enabled</c> is read where
    /// the hosted service starts, and a caller combines the two. Keeping them apart is what stops
    /// a <c>true</c> here from being read as "this job is running".
    /// </para>
    /// </summary>
    public interface IJobSwitch
    {
        /// <summary>Cached, so a one-minute loop does not hit the database every tick for a value
        /// that changes twice a month. The cache window bounds how long a toggle takes to apply.</summary>
        Task<bool> IsEnabledAsync(string job, CancellationToken ct);

        /// <summary>The full row, for the admin endpoint. Uncached — an operator reading the page
        /// after pressing the button must see what they just did, not a stale cache entry.</summary>
        Task<JobSwitchState> GetAsync(string job, CancellationToken ct);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build FlightAssistant.Application/FlightAssistant.Application.csproj`
Expected: succeeds, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add FlightAssistant.Application/Jobs FlightAssistant.Application/Interfaces/Jobs
git commit -m "feat(jobs): add the job switch contract (#471)"
```

---

### Task 2: The `job_switches` table

**Files:**
- Create: `FlightAssistant.Domain/Jobs/Entities/JobSwitch.cs`
- Create: `FlightAssistant.Infrastructure/Persistance/Configurations/Jobs/JobSwitchConfiguration.cs`
- Modify: `FlightAssistant.Infrastructure/Persistance/FlightAssistantDbContext.cs`
- Modify: `FlightAssistant.Infrastructure/Persistance/AdminAuditInterceptor.cs`
- Modify: `FlightAssistant.Infrastructure.Tests/Persistance/AdminAuditInterceptorTests.cs`
- Create: a migration under `FlightAssistant.Infrastructure/Persistance/Migrations/`

**Interfaces:**
- Consumes: `JobNames` (Task 1).
- Produces: entity `JobSwitch` with settable `Job`, `Enabled`, `ChangedUtc`, `ChangedBy`, `Reason`; `DbSet<JobSwitch> JobSwitches`.

- [ ] **Step 1: Create the entity**

```csharp
namespace FlightAssistant.Domain.Jobs.Entities
{
    /// <summary>
    /// One background job's runtime on/off switch. Persisted rather than held in memory so a
    /// toggle survives a restart: a deploy silently re-enabling a job an operator stopped is the
    /// failure they would least expect and least quickly notice.
    /// </summary>
    public sealed class JobSwitch
    {
        public string Job { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        public DateTime? ChangedUtc { get; set; }

        public string? ChangedBy { get; set; }

        /// <summary>Optional operator note, rendered on the health page beside the state.</summary>
        public string? Reason { get; set; }
    }
}
```

- [ ] **Step 2: Create the EF configuration**

```csharp
using FlightAssistant.Domain.Jobs.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Jobs
{
    /// <summary>
    /// The <c>job_switches</c> table: one row per job, keyed by the job name. snake_case columns,
    /// matching <c>verification_requests</c> and <c>data_source_status</c>.
    /// </summary>
    public sealed class JobSwitchConfiguration : IEntityTypeConfiguration<JobSwitch>
    {
        public void Configure(EntityTypeBuilder<JobSwitch> b)
        {
            b.ToTable("job_switches");

            // The job name is the natural key: there is exactly one row per job and every read
            // and write addresses it by name.
            b.HasKey(x => x.Job);

            b.Property(x => x.Job).HasColumnName("job").HasMaxLength(64);
            b.Property(x => x.Enabled).HasColumnName("enabled");
            b.Property(x => x.ChangedUtc).HasColumnName("changed_utc");
            b.Property(x => x.ChangedBy).HasColumnName("changed_by").HasMaxLength(256);
            b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(512);
        }
    }
}
```

- [ ] **Step 3: Register the DbSet**

Add to `FlightAssistantDbContext`, beside the existing sets:

```csharp
public DbSet<JobSwitch> JobSwitches => Set<JobSwitch>();
```

- [ ] **Step 4: Exclude it from the audit interceptor**

Add `JobSwitch` to the exclusion set in `AdminAuditInterceptor`, beside `AdminAuditLog`, `VerificationRequest` and `DataSourceStatus`, with this comment:

```csharp
// - JobSwitch (#471): excluded for the OPPOSITE reason to DataSourceStatus above. That one is
//   written constantly and automatically; this one is written rarely and deliberately, and its
//   writes are audited EXPLICITLY by ToggleJobCommandHandler so the operator's reason text can
//   be carried. Letting the interceptor fire too would put two rows in the audit log for one
//   toggle, saying different things.
```

- [ ] **Step 5: Pin the exclusion with a test**

`AdminAuditInterceptorTests` already pins the other exclusions (#469). Add both shapes beside them — an insert and an update, because seeding writes one and every toggle writes the other:

```csharp
[Fact]
public async Task SaveChangesAsync_JobSwitch_WritesNoAuditRow()
{
    var dbName = Guid.NewGuid().ToString();

    using var db = CreateContext(dbName, AdminActor());
    db.JobSwitches.Add(new JobSwitch { Job = "flight-sync", Enabled = true });
    await db.SaveChangesAsync();

    Assert.Empty(await db.AdminAuditLogs.ToListAsync());
}

// A toggle is an update, and the update is the shape that would double-audit against the
// explicit row the command handler writes.
[Fact]
public async Task SaveChangesAsync_UpdatedJobSwitch_WritesNoAuditRow()
{
    var dbName = Guid.NewGuid().ToString();

    using (var seed = CreateContext(dbName, AdminActor()))
    {
        seed.JobSwitches.Add(new JobSwitch { Job = "flight-sync", Enabled = true });
        await seed.SaveChangesAsync();
    }

    using var db = CreateContext(dbName, AdminActor());
    var row = await db.JobSwitches.SingleAsync();
    row.Enabled = false;
    await db.SaveChangesAsync();

    Assert.Empty(await db.AdminAuditLogs.ToListAsync());
}
```

- [ ] **Step 6: Run those tests to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter AdminAuditInterceptorTests`
Expected: the two new tests FAIL — the interceptor writes rows until the exclusion is added. If they pass before you add the exclusion, the test is not exercising what it claims; stop and find out why.

- [ ] **Step 7: Add the exclusion, re-run**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter AdminAuditInterceptorTests`
Expected: PASS, including the pre-existing exclusion tests.

- [ ] **Step 8: Create the migration**

```bash
dotnet ef migrations add AddJobSwitches \
  --project FlightAssistant.Infrastructure \
  --startup-project FlightAssistant.API
```

Open the generated file and confirm it creates `job_switches` with a primary key on `job` and no other index. Report what it generated.

- [ ] **Step 9: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS, no regressions.

```bash
git add FlightAssistant.Domain/Jobs FlightAssistant.Infrastructure/Persistance
git commit -m "feat(jobs): add the job_switches table (#471)"
```

---

### Task 3: The switch service

**Files:**
- Create: `FlightAssistant.Infrastructure/Jobs/CachedJobSwitch.cs` — named `CachedJobSwitch`, not `JobSwitch`, because the EF entity from Task 2 already owns that name
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs`
- Test: `FlightAssistant.Infrastructure.Tests/Jobs/CachedJobSwitchTests.cs`

**Interfaces:**
- Consumes: `IJobSwitch`, `JobNames`, `JobSwitchState` (Task 1); `JobSwitch` entity (Task 2).
- Produces: `CachedJobSwitch` (singleton) with `public static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(10)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using FlightAssistant.Application.Jobs;
using FlightAssistant.Infrastructure.Jobs;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FlightAssistant.Infrastructure.Tests.Jobs
{
    public class CachedJobSwitchTests
    {
        [Fact]
        public async Task AJobWithNoRow_IsEnabled()
        {
            // An upgrade must not stop a running job. A missing row means "nobody has ever
            // touched this switch", which is enabled.
            var (sut, _, _) = Build(rows: []);

            Assert.True(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));
        }

        [Fact]
        public async Task AJobSwitchedOff_IsNotEnabled()
        {
            var (sut, _, _) = Build(rows: [new JobSwitchRow(JobNames.FlightSync, false)]);

            Assert.False(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));
        }

        [Fact]
        public async Task RepeatedReadsInsideTheWindow_HitTheDatabaseOnce()
        {
            var (sut, _, reads) = Build(rows: [new JobSwitchRow(JobNames.FlightSync, true)]);

            await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None);
            await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None);
            await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None);

            Assert.Equal(1, reads.Count);
        }

        // The window is what bounds how long a toggle takes to take effect, so it is asserted
        // rather than left to chance.
        [Fact]
        public async Task AfterTheWindow_TheDatabaseIsReadAgain()
        {
            var (sut, time, reads) = Build(rows: [new JobSwitchRow(JobNames.FlightSync, true)]);
            await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None);

            time.Advance(CachedJobSwitch.CacheWindow + TimeSpan.FromSeconds(1));
            await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None);

            Assert.Equal(2, reads.Count);
        }

        [Fact]
        public async Task ATogglePastTheWindow_IsObserved()
        {
            var rows = new List<JobSwitchRow> { new(JobNames.FlightSync, true) };
            var (sut, time, _) = Build(rows);
            Assert.True(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));

            rows[0] = new JobSwitchRow(JobNames.FlightSync, false);
            time.Advance(CachedJobSwitch.CacheWindow + TimeSpan.FromSeconds(1));

            Assert.False(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));
        }

        // Each job caches separately, or switching one off would silently stop the other for a
        // window.
        [Fact]
        public async Task EachJobIsCachedSeparately()
        {
            var (sut, _, _) = Build(rows:
            [
                new JobSwitchRow(JobNames.FlightSync, false),
                new JobSwitchRow(JobNames.Verification, true),
            ]);

            Assert.False(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));
            Assert.True(await sut.IsEnabledAsync(JobNames.Verification, CancellationToken.None));
        }

        // Diagnostics must not become an outage: a database blip must not stop a running job.
        [Fact]
        public async Task AFailedRead_LeavesTheJobEnabled()
        {
            var (sut, _, _) = Build(rows: [], throwOnRead: true);

            Assert.True(await sut.IsEnabledAsync(JobNames.FlightSync, CancellationToken.None));
        }

        [Fact]
        public async Task GetAsync_IsNotCached()
        {
            var (sut, _, reads) = Build(rows: [new JobSwitchRow(JobNames.FlightSync, true)]);

            await sut.GetAsync(JobNames.FlightSync, CancellationToken.None);
            await sut.GetAsync(JobNames.FlightSync, CancellationToken.None);

            Assert.Equal(2, reads.Count);
        }
    }
}
```

Write `JobSwitchRow` and `Build(...)` as small in-file helpers: a fake reader over an in-memory list that records each read into a `reads` list, plus a `FakeTimeProvider`. Follow the fake style already used in `BufferedDataSourceObserverTests`. Do **not** stand up a real database for these — the point is the caching policy, not the query.

To make that possible, put the database read behind a narrow seam the test can substitute (an internal delegate or a tiny reader interface the real implementation resolves from `IServiceScopeFactory`). Decide the shape yourself and say in your report what you chose and why.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter CachedJobSwitchTests`
Expected: FAIL — `CachedJobSwitch` does not exist.

- [ ] **Step 3: Implement**

Key points, which the tests above pin:

- Singleton, holding `IServiceScopeFactory` (the DbContext is scoped) and `TimeProvider`.
- A per-job cache entry of `(bool Enabled, DateTimeOffset ReadUtc)`; a read inside `CacheWindow` is served from it.
- A missing row means enabled.
- A read that throws is logged at warning and returns **enabled**, then retried after the window. Failing open is deliberate: this is a stop switch, and a database blip must not stop a healthy job. Say so in a comment.
- `GetAsync` bypasses the cache.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FlightAssistant.Infrastructure.Tests --filter CachedJobSwitchTests`
Expected: PASS, 8 tests.

- [ ] **Step 5: Prove the cache tests bite**

Temporarily make `IsEnabledAsync` read through on every call (ignore the cache) and re-run. Expected: `RepeatedReadsInsideTheWindow_HitTheDatabaseOnce` fails. Restore and re-run to green.

- [ ] **Step 6: Register and commit**

In `DependencyInjection.AddInfrastructure`:

```csharp
services.AddSingleton<IJobSwitch, CachedJobSwitch>();
```

```bash
git add FlightAssistant.Infrastructure/Jobs FlightAssistant.Infrastructure.Tests/Jobs FlightAssistant.Infrastructure/DependencyInjection.cs
git commit -m "feat(jobs): read the job switch, cached per tick (#471)"
```

---

### Task 4: Heartbeat `SwitchedOff` states

**Files:**
- Modify: `FlightAssistant.API/HostedServices/FlightSyncHeartbeat.cs`
- Modify: `FlightAssistant.API/HostedServices/VerificationJobHeartbeat.cs`
- Test: `FlightAssistant.API.Tests/HostedServices/HeartbeatSwitchedOffTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `FlightSyncOutcome.SwitchedOff`, `VerificationJobOutcome.SwitchedOff`, and `MarkSwitchedOff()` on both heartbeats.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void FlightSync_MarkSwitchedOff_ReportsSwitchedOff()
{
    var heartbeat = new FlightSyncHeartbeat(TimeProvider.System);

    heartbeat.MarkSwitchedOff();

    Assert.Equal(FlightSyncOutcome.SwitchedOff, heartbeat.Current.LastOutcome);
}

// The counts describe the last real run. A switched-off tick did no work, so it must not
// overwrite them with zeroes and claim a run that processed nothing.
[Fact]
public void FlightSync_MarkSwitchedOff_LeavesTheLastRunsCountsAlone()
{
    var heartbeat = new FlightSyncHeartbeat(TimeProvider.System);
    heartbeat.MarkRunStarted();
    heartbeat.MarkRunSucceeded(processed: 7, failed: 1, nextRunDueUtc: DateTime.UtcNow);

    heartbeat.MarkSwitchedOff();

    Assert.Equal(7, heartbeat.Current.FlightsProcessed);
    Assert.Equal(1, heartbeat.Current.FlightsFailed);
}

[Fact]
public void Verification_MarkSwitchedOff_IsNotPaused()
{
    var heartbeat = new VerificationJobHeartbeat(TimeProvider.System);

    heartbeat.MarkSwitchedOff();

    // Paused means the cycle ran and held work back for budget. An operator stop is a
    // different situation with a different remedy, and must not read as the same thing.
    Assert.Equal(VerificationJobOutcome.SwitchedOff, heartbeat.Current.LastOutcome);
    Assert.NotEqual(VerificationJobOutcome.Paused, heartbeat.Current.LastOutcome);
}
```

Check both heartbeat constructors' real signatures before writing these — `FlightSyncHeartbeat` and `VerificationJobHeartbeat` each take a `TimeProvider`, and `MarkRunSucceeded` differs between them.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlightAssistant.API.Tests --filter HeartbeatSwitchedOffTests`
Expected: FAIL — `SwitchedOff` and `MarkSwitchedOff` do not exist.

- [ ] **Step 3: Implement**

Add `SwitchedOff` to both enums, and a `MarkSwitchedOff()` to each heartbeat that publishes a new snapshot with `LastOutcome = SwitchedOff` and **every other field carried over unchanged**. Follow the existing `Mark*` methods exactly: take the lock, build a new immutable snapshot, one atomic reference write.

Add this comment to each enum:

```csharp
// SwitchedOff (#471): an admin stopped this job from the panel. Distinct from Paused on the
// verification job, which means the cycle RAN and held work back because the AeroAPI budget is
// exhausted -- "we chose not to spend" and "an operator stopped us" need different responses.
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FlightAssistant.API.Tests --filter HeartbeatSwitchedOffTests`
Expected: PASS, 3 tests.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS. Watch anything switching over these enums — `AdminHealthReportBuilder` maps them to strings, and a new member must not break an existing mapping.

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.API/HostedServices FlightAssistant.API.Tests/HostedServices
git commit -m "feat(jobs): let a heartbeat report an operator stop (#471)"
```

---

### Task 5: Restructure the flight-sync loop

**This is the task that carries the risk in this plan.** The service currently proves it is off by not existing; afterwards it proves it by deciding, every tick, not to act. A wrong decision is invisible until someone reads a bill or notices stale flight data.

**Files:**
- Modify: `FlightAssistant.API/HostedServices/FlightStatusSyncBackgroundService.cs:35-60`
- Test: `FlightAssistant.API.Tests/HostedServices/FlightSyncSwitchTests.cs`

**Interfaces:**
- Consumes: `IJobSwitch`, `JobNames` (Task 1); `MarkSwitchedOff()` (Task 4).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

```csharp
// The whole point: a switched-off loop must not spend money.
[Fact]
public async Task SwitchedOff_RunsNoCycle()
{
    var (service, mediator, time) = Build(switchEnabled: false);

    await StartAndTickAsync(service, time, ticks: 3);

    // No cycle ran, so nothing was sent through MediatR and no AeroAPI call could have happened.
    Assert.Empty(mediator.Sent);
}

[Fact]
public async Task SwitchedOff_MarksTheHeartbeatSwitchedOff()
{
    var (service, _, time) = Build(switchEnabled: false, out var heartbeat);

    await StartAndTickAsync(service, time, ticks: 1);

    Assert.Equal(FlightSyncOutcome.SwitchedOff, heartbeat.Current.LastOutcome);
}

// A loop that exits when switched off can never be switched back on.
[Fact]
public async Task SwitchedBackOn_ResumesWithoutARestart()
{
    var jobSwitch = new FakeJobSwitch(enabled: false);
    var (service, mediator, time) = Build(jobSwitch);
    await StartAndTickAsync(service, time, ticks: 2);
    Assert.Empty(mediator.Sent);

    jobSwitch.Enabled = true;
    await StartAndTickAsync(service, time, ticks: 1);

    Assert.NotEmpty(mediator.Sent);
}

[Fact]
public async Task SwitchedOn_RunsTheCycleAsBefore()
{
    var (service, mediator, time) = Build(switchEnabled: true);

    await StartAndTickAsync(service, time, ticks: 1);

    Assert.NotEmpty(mediator.Sent);
}

// The config ceiling keeps its current behaviour exactly: the service terminates, and the
// heartbeat stays NeverRun so /admin/health reads "disabled" rather than "broken".
[Fact]
public async Task ConfigDisabled_TerminatesTheServiceAndLeavesTheHeartbeatNeverRun()
{
    var (service, mediator, time) = Build(configEnabled: false, switchEnabled: true, out var heartbeat);

    await service.StartAsync(CancellationToken.None);
    await service.ExecuteTask!;   // completes immediately when config-disabled

    Assert.Empty(mediator.Sent);
    Assert.Equal(FlightSyncOutcome.NeverRun, heartbeat.Current.LastOutcome);
}

// A config-disabled service must not read the switch at all — there is nothing it could change.
[Fact]
public async Task ConfigDisabled_NeverReadsTheSwitch()
{
    var jobSwitch = new FakeJobSwitch(enabled: true);
    var (service, _, _) = Build(jobSwitch, configEnabled: false);

    await service.StartAsync(CancellationToken.None);
    await service.ExecuteTask!;

    Assert.Equal(0, jobSwitch.Reads);
}
```

Write `FakeJobSwitch` (a mutable `Enabled` plus a `Reads` counter), a recording MediatR fake, and `StartAndTickAsync` (starts the service and advances a `FakeTimeProvider` by `LoopInterval` per tick, yielding so the loop runs) as in-file helpers.

**Note the existing loop shape before you start:** it is `do { RunAndRecordAsync } while (await timer.WaitForNextTickAsync(...))` — it runs once immediately, *then* waits. Your test helper must account for that first immediate run, and your restructure must preserve it: a service that starts up should sync straight away, not wait a full interval.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlightAssistant.API.Tests --filter FlightSyncSwitchTests`
Expected: FAIL — the service does not consult a switch.

- [ ] **Step 3: Implement the restructure**

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    // Unchanged (#471): the config ceiling still terminates the service. Nothing reachable from
    // the admin panel can lift it, so there is no reason to keep a loop alive for it — and the
    // heartbeat still stays NeverRun, which is what makes /admin/health say "disabled" here
    // rather than "broken".
    if (!_options.Enabled)
    {
        _log.LogInformation("FlightStatusSyncBackgroundService is disabled via config.");
        return;
    }

    using var timer = new PeriodicTimer(_options.LoopInterval, _timeProvider);
    try
    {
        do
        {
            // Read per tick, not once: the switch is what an admin can change while this loop
            // is running, and a loop that read it once could never be switched back on.
            if (!await _jobSwitch.IsEnabledAsync(JobNames.FlightSync, stoppingToken))
            {
                _heartbeat.MarkSwitchedOff();
                continue;
            }

            await RunAndRecordAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
        // Clean shutdown, not a failed run — heartbeat is left as-is.
    }
}
```

Inject `IJobSwitch` into the constructor beside the existing dependencies.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FlightAssistant.API.Tests --filter FlightSyncSwitchTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Prove the tests bite — do this one for real**

Temporarily remove the `continue` so a switched-off tick falls through into `RunAndRecordAsync`. Re-run. Expected: `SwitchedOff_RunsNoCycle` fails. Restore and re-run to green.

This is the single most important verification in the plan: that test is what stands between a switched-off job and a surprise AeroAPI bill. Report exactly what you saw.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add FlightAssistant.API/HostedServices/FlightStatusSyncBackgroundService.cs FlightAssistant.API.Tests/HostedServices/FlightSyncSwitchTests.cs
git commit -m "feat(jobs): let the flight sync loop be switched off (#471)"
```

---

### Task 6: Restructure the verification loop

**Files:**
- Modify: `FlightAssistant.API/HostedServices/ReferenceVerificationBackgroundService.cs:49-58`
- Test: `FlightAssistant.API.Tests/HostedServices/VerificationSwitchTests.cs`

**Interfaces:**
- Consumes: `IJobSwitch`, `JobNames` (Task 1); `MarkSwitchedOff()` (Task 4).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

```csharp
// The whole point: a switched-off loop must not spend money.
[Fact]
public async Task SwitchedOff_RunsNoCycle()
{
    var (service, mediator, time) = Build(switchEnabled: false);

    await StartAndTickAsync(service, time, ticks: 3);

    Assert.Empty(mediator.Sent);
}

[Fact]
public async Task SwitchedOff_MarksTheHeartbeatSwitchedOff()
{
    var (service, _, time) = Build(switchEnabled: false, out var heartbeat);

    await StartAndTickAsync(service, time, ticks: 1);

    Assert.Equal(VerificationJobOutcome.SwitchedOff, heartbeat.Current.LastOutcome);
}

// SwitchedOff and Paused are different situations. A switched-off cycle never ran; a paused one
// ran and held work back because the AeroAPI budget is exhausted. Reporting one as the other
// sends an operator to the wrong remedy.
[Fact]
public async Task SwitchedOff_DoesNotReportPaused()
{
    var (service, _, time) = Build(switchEnabled: false, out var heartbeat);

    await StartAndTickAsync(service, time, ticks: 1);

    Assert.NotEqual(VerificationJobOutcome.Paused, heartbeat.Current.LastOutcome);
}

// A loop that exits when switched off can never be switched back on.
[Fact]
public async Task SwitchedBackOn_ResumesWithoutARestart()
{
    var jobSwitch = new FakeJobSwitch(enabled: false);
    var (service, mediator, time) = Build(jobSwitch);
    await StartAndTickAsync(service, time, ticks: 2);
    Assert.Empty(mediator.Sent);

    jobSwitch.Enabled = true;
    await StartAndTickAsync(service, time, ticks: 1);

    Assert.NotEmpty(mediator.Sent);
}

[Fact]
public async Task SwitchedOn_RunsTheCycleAsBefore()
{
    var (service, mediator, time) = Build(switchEnabled: true);

    await StartAndTickAsync(service, time, ticks: 1);

    Assert.NotEmpty(mediator.Sent);
}

// The config ceiling keeps its current behaviour exactly: the service terminates, and the
// heartbeat stays NeverRun so /admin/health reads "disabled" rather than "broken".
[Fact]
public async Task ConfigDisabled_TerminatesTheServiceAndLeavesTheHeartbeatNeverRun()
{
    var (service, mediator, _) = Build(configEnabled: false, switchEnabled: true, out var heartbeat);

    await service.StartAsync(CancellationToken.None);
    await service.ExecuteTask!;

    Assert.Empty(mediator.Sent);
    Assert.Equal(VerificationJobOutcome.NeverRun, heartbeat.Current.LastOutcome);
}

// A config-disabled service must not read the switch at all — there is nothing it could change.
[Fact]
public async Task ConfigDisabled_NeverReadsTheSwitch()
{
    var jobSwitch = new FakeJobSwitch(enabled: true);
    var (service, _, _) = Build(jobSwitch, configEnabled: false);

    await service.StartAsync(CancellationToken.None);
    await service.ExecuteTask!;

    Assert.Equal(0, jobSwitch.Reads);
}
```

Write `FakeJobSwitch`, the recording MediatR fake and `StartAndTickAsync` as in-file helpers, as in Task 5 — this test file stands on its own and shares nothing with that one.

**Note this service's loop shape**: it is `while (!stoppingToken.IsCancellationRequested)` with `MarkRunStarted()` as the first statement in the body — a different shape from the flight sync's `do/while` with a `PeriodicTimer`. Read it before restructuring; the switch check must go **before** `MarkRunStarted()`, or a switched-off tick will report a run that never happened.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test FlightAssistant.API.Tests --filter VerificationSwitchTests`
Expected: FAIL.

- [ ] **Step 3: Implement, keeping the config branch exactly as it is**

The config branch keeps its current behaviour and its comment, unchanged. The switch check goes at the top of the loop body — **before `MarkRunStarted()`**, or a switched-off tick reports a run that never happened:

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    // Read per tick, before MarkRunStarted: this tick may do nothing, and marking a run
    // started before knowing that would put a run in the heartbeat that never happened.
    if (!await _jobSwitch.IsEnabledAsync(JobNames.Verification, stoppingToken))
    {
        _heartbeat.MarkSwitchedOff();
        await DelayUntilNextCycleAsync(stoppingToken);   // the loop's existing wait
        continue;
    }

    _heartbeat.MarkRunStarted();
    // … the existing cycle, unchanged
}
```

Read the real loop before editing: it waits out its interval differently from the flight sync's `PeriodicTimer`, and the switched-off path must use whatever that loop already does rather than introducing a second waiting mechanism. Name what you found in your report.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test FlightAssistant.API.Tests --filter VerificationSwitchTests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Prove the tests bite**

Temporarily let a switched-off tick fall through into the cycle. Re-run; `SwitchedOff_RunsNoCycle` must fail. Restore and re-run to green. Report what you saw.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add FlightAssistant.API/HostedServices/ReferenceVerificationBackgroundService.cs FlightAssistant.API.Tests/HostedServices/VerificationSwitchTests.cs
git commit -m "feat(jobs): let the verification loop be switched off (#471)"
```

---

### Task 7: Manual triggers keep working

No production change — this task exists to pin a deliberate product decision so a later tidy-up cannot quietly remove it.

**Files:**
- Test: `FlightAssistant.API.Tests/Integration/ManualTriggersIgnoreSwitchTests.cs`

**Interfaces:**
- Consumes: `JobNames` (Task 1), the `job_switches` table (Task 2).
- Produces: nothing.

- [ ] **Step 1: Write the tests**

```csharp
// Switching a job off stops its SCHEDULED loop. It does not seal the feature: an operator who
// switched a job off needs a way to test it before switching it back on, and taking that away
// would make the toggle something people hesitate to use.
[Fact]
public async Task ManualFlightSync_StillWorks_WhileTheLoopIsSwitchedOff()
{
    await SwitchOffAsync(JobNames.FlightSync);

    var response = await AdminClient.PostAsync($"/admin/flights/{TrackedFlightId}/sync", null);

    Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
    Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
}

[Fact]
public async Task ManualVerification_StillWorks_WhileTheLoopIsSwitchedOff()
{
    await SwitchOffAsync(JobNames.Verification);

    var response = await AdminClient.PostAsJsonAsync("/admin/verification/requests", NewRequest());

    Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
    Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
}
```

Follow the existing integration-test fixture (`FlightAssistantWebApplicationFactory`); read `FlightEndpointTests` for how a tracked flight is seeded and how the admin client is built. Assert on "not refused" rather than a specific success code, so the test pins the decision without becoming a brittle restatement of each endpoint's happy path.

- [ ] **Step 2: Run them**

Run: `dotnet test FlightAssistant.API.Tests --filter ManualTriggersIgnoreSwitchTests`
Expected: PASS immediately — manual triggers never consulted the switch. If either fails, something in Tasks 5–6 wired the switch too broadly; fix that rather than the test.

- [ ] **Step 3: Commit**

```bash
git add FlightAssistant.API.Tests/Integration/ManualTriggersIgnoreSwitchTests.cs
git commit -m "test(jobs): pin that manual triggers ignore the switch (#471)"
```

---

### Task 8: `GET /admin/jobs` and `PUT /admin/jobs/{job}`

**Files:**
- Create: `FlightAssistant.Contracts/Responses/Admin/AdminJobDto.cs`
- Create: `FlightAssistant.Application/Queries/Admin/Jobs/GetJobsQuery.cs`
- Create: `FlightAssistant.Application/Commands/Admin/Jobs/ToggleJobCommand.cs`
- Create: `FlightAssistant.API/Endpoints/Admin/AdminJobEndpoints.cs`
- Modify: `FlightAssistant.API/Program.cs` (map the group)
- Test: `FlightAssistant.Application.Tests/Queries/Admin/Jobs/GetJobsQueryTests.cs`
- Test: `FlightAssistant.API.Tests/Integration/AdminJobEndpointTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: `AdminJobDto` — the exact field list the panel binds to in Phase 2.

- [ ] **Step 1: Define the DTO**

```csharp
namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <param name="ConfigEnabled">The deployment ceiling. False means this job cannot run in this
    /// environment at all and no toggle can start it.</param>
    /// <param name="SwitchEnabled">The runtime switch alone.</param>
    /// <param name="EffectiveEnabled"><c>ConfigEnabled &amp;&amp; SwitchEnabled</c>. Derived here so
    /// the panel renders one answer rather than combining two and risking a different rule.</param>
    /// <param name="CanToggle">False exactly when <paramref name="ConfigEnabled"/> is false. The
    /// panel disables the control and shows why, rather than hiding it — a missing control makes an
    /// operator hunt for a feature that is present but barred.</param>
    public sealed record AdminJobDto(
        string Job,
        string DisplayName,
        bool ConfigEnabled,
        bool SwitchEnabled,
        bool EffectiveEnabled,
        bool CanToggle,
        DateTime? ChangedUtc,
        string? ChangedBy,
        string? Reason,
        string? LastOutcome,
        DateTime? LastRunUtc);
}
```

- [ ] **Step 2: Write the failing query tests**

```csharp
[Theory]
[InlineData(true,  true,  true)]
[InlineData(true,  false, false)]
[InlineData(false, true,  false)]   // config is a hard ceiling
[InlineData(false, false, false)]
public async Task EffectiveEnabled_IsConfigAndSwitch(bool config, bool @switch, bool expected)
{
    var result = await BuildHandler(configEnabled: config, switchEnabled: @switch)
        .Handle(new GetJobsQuery(), CancellationToken.None);

    Assert.Equal(expected, result.Single(j => j.Job == JobNames.FlightSync).EffectiveEnabled);
}

[Fact]
public async Task CanToggle_IsFalse_WhenConfigHasTheJobOff()
{
    var result = await BuildHandler(configEnabled: false, switchEnabled: true)
        .Handle(new GetJobsQuery(), CancellationToken.None);

    Assert.False(result.Single(j => j.Job == JobNames.FlightSync).CanToggle);
}

[Fact]
public async Task ReturnsBothJobs()
{
    var result = await BuildHandler(configEnabled: true, switchEnabled: true)
        .Handle(new GetJobsQuery(), CancellationToken.None);

    Assert.Equal(JobNames.All.Count, result.Count);
}
```

- [ ] **Step 3: Run to verify they fail, then implement the query**

Run: `dotnet test FlightAssistant.Application.Tests --filter GetJobsQueryTests`
Expected: FAIL.

The handler reads `FlightSync:Enabled` / `Verification:Enabled` from `IConfiguration` (the same keys and defaults the services read — `true` for both), the switch rows via `IJobSwitch.GetAsync`, and the last outcome and run time from the two heartbeats.

- [ ] **Step 4: Implement the toggle command**

`ToggleJobCommand(string Job, bool Enabled, string? Reason)`:

- unknown job → `DomainException` 404
- `configEnabled == false` → `DomainException` **409**, message naming the config key. Not 403: the caller is permitted, the environment is what refuses.
- otherwise upsert the row with `ChangedUtc`, `ChangedBy` (from `IAdminContext.ActorId`/`ActorName`) and `Reason`
- write an `AdminAuditLog` row **explicitly** via `AdminAuditLog.Create(...)` with `AdminAuditAction.Update`, `entityType` `"JobSwitch"`, `entityId` the job name, and a summary naming the new state and the reason

- [ ] **Step 5: Write the failing integration tests**

```csharp
[Fact]
public async Task Put_RequiresAdmin()
{
    var response = await AnonymousClient.PutAsJsonAsync("/admin/jobs/flight-sync", new { enabled = false });
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
}

[Fact]
public async Task Put_SwitchesTheJobOff_AndReportsIt()
{
    var response = await AdminClient.PutAsJsonAsync("/admin/jobs/flight-sync",
        new { enabled = false, reason = "burning AeroAPI budget" });

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var job = await response.Content.ReadFromJsonAsync<AdminJobDto>();
    Assert.False(job!.SwitchEnabled);
    Assert.False(job.EffectiveEnabled);
    Assert.Equal("burning AeroAPI budget", job.Reason);
}

// The config ceiling, enforced at the API rather than only in the UI.
[Fact]
public async Task Put_Returns409_WhenConfigHasTheJobOff()
{
    using var factory = FactoryWithConfig(("FlightSync:Enabled", "false"));

    var response = await AdminClientFor(factory)
        .PutAsJsonAsync("/admin/jobs/flight-sync", new { enabled = true });

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
}

[Fact]
public async Task Put_Returns409_AndWritesNoSwitchRow_WhenConfigHasTheJobOff()
{
    using var factory = FactoryWithConfig(("FlightSync:Enabled", "false"));

    await AdminClientFor(factory).PutAsJsonAsync("/admin/jobs/flight-sync", new { enabled = true });

    Assert.Empty(await DbFor(factory).JobSwitches.Where(s => s.Job == "flight-sync").ToListAsync());
}

[Fact]
public async Task Put_Returns404_ForAnUnknownJob()
{
    var response = await AdminClient.PutAsJsonAsync("/admin/jobs/not-a-job", new { enabled = false });
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}

// One row, not two: the interceptor exclusion from Task 2 is what makes this hold.
[Fact]
public async Task Put_WritesExactlyOneAuditRow_CarryingTheReason()
{
    await AdminClient.PutAsJsonAsync("/admin/jobs/flight-sync",
        new { enabled = false, reason = "burning AeroAPI budget" });

    var rows = await Db.AdminAuditLogs.Where(a => a.EntityType == "JobSwitch").ToListAsync();
    Assert.Single(rows);
    Assert.Contains("burning AeroAPI budget", rows[0].Summary);
}
```

- [ ] **Step 6: Map the endpoints**

On a new `jobs` group off the shared `/admin` group (which already carries `RequireAuthorization("AdminOnly")` and `RequireCors("AdminPanel")` — do not re-apply them):

```csharp
group.MapGet("", …)
.WithName("AdminGetJobs")
.WithSummary("The background jobs and whether they are running")
.WithDescription(
    "Both scheduled jobs: the deployment config ceiling, the runtime switch, and the effective " +
    "state, alongside who last changed the switch and why. 'canToggle' is false when config has " +
    "the job off — config is a hard ceiling and no toggle can start a job it has disabled.")
.Produces<List<AdminJobDto>>(200);

group.MapPut("{job}", …)
.WithName("AdminToggleJob")
.WithSummary("Switch a background job on or off")
.WithDescription(
    "Stops or starts a job's scheduled loop, taking effect within about ten seconds. Manual " +
    "triggers are unaffected and keep working while a job is switched off. Audited. 409 when " +
    "config has the job off, 404 when the job name is unknown.")
.Produces<AdminJobDto>(200)
.ProducesProblem(404)
.ProducesProblem(409);
```

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 8: Verify by hand against a running API**

Start the API, `PUT /admin/jobs/flight-sync` with `enabled: false`, and watch the logs: within about ten seconds the sync loop must stop running cycles, and `/admin/health` must report the flight sync as switched off rather than running or broken. Switch it back on and confirm cycles resume without a restart.

This is the one behaviour no unit test can fully prove — the tests use a fake clock, and the real cache window plus the real loop interval only meet in a running process. Report what you saw.

- [ ] **Step 9: Commit and open the PR**

```bash
git add FlightAssistant.Contracts FlightAssistant.Application FlightAssistant.API FlightAssistant.Application.Tests FlightAssistant.API.Tests
git commit -m "feat(jobs): read and set the job switches over the admin API (#471)"
git push -u origin feature/<n>-job-switches
gh pr create --base development --title "feat(jobs): switch the background jobs on and off (#471)"
```

---

# Phase 2 — Admin panel (PR 2)

**Do not start until Phase 1 has merged** and the OpenAPI client has been regenerated. Phase 2 runs in the **`flight-assistant-admin-panel` repository**; open an issue there when Phase 1 merges and substitute its number for `#<panel issue>` below. That is the only deliberately unfilled value in this plan.

### Task 9: The Jobs section

**Files:**
- Modify: `openapi/openapi.json` (regenerated), then `npm run api:generate`
- Create: `src/pages/health/JobsSection.tsx`
- Modify: `src/pages/health/SystemHealthPage.tsx`
- Test: `src/pages/health/JobsSection.test.tsx`

- [ ] **Step 1: Regenerate the client**

```bash
npm run api:sync && npm run api:generate
```

Confirm `src/api/generated/model/adminJobDto.ts` appears with the fields from Task 8.

- [ ] **Step 2: Write the failing tests**

```tsx
it('shows a running job as on', async () => {
  renderWith([job({ job: 'flight-sync', effectiveEnabled: true, canToggle: true })])

  expect(await screen.findByRole('switch', { name: /flight sync/i })).toBeChecked()
})

// A deliberate stop is not a fault. Same rule the data-source rows follow, where
// NotConfigured is gray rather than red.
it('shows a switched-off job in amber, not red', async () => {
  renderWith([job({ job: 'flight-sync', switchEnabled: false, effectiveEnabled: false })])

  expect(await screen.findByText(/switched off/i)).toHaveClass('text-amber-600')
})

it('says who switched a job off and why', async () => {
  renderWith([job({
    switchEnabled: false, effectiveEnabled: false,
    changedBy: 'salihalishov@gmail.com', reason: 'burning AeroAPI budget',
  })])

  expect(await screen.findByText(/salihalishov@gmail.com/)).toBeInTheDocument()
  expect(screen.getByText(/burning AeroAPI budget/)).toBeInTheDocument()
})

// The control is disabled and explained, never hidden: a missing control makes an operator
// hunt for a feature that is present but barred.
it('disables the toggle when config has the job off, and says why', async () => {
  renderWith([job({ configEnabled: false, effectiveEnabled: false, canToggle: false })])

  expect(await screen.findByRole('switch', { name: /flight sync/i })).toBeDisabled()
  expect(screen.getByText(/FlightSync:Enabled/)).toBeInTheDocument()
})

it('says that manual runs still work while a job is off', async () => {
  renderWith([job({ switchEnabled: false, effectiveEnabled: false })])

  expect(await screen.findByText(/manual .* still/i)).toBeInTheDocument()
})

it('reports a refused toggle without claiming the job changed', async () => {
  server.use(http.put('*/admin/jobs/flight-sync', () => new HttpResponse(null, { status: 409 })))
  renderWith([job({ canToggle: true })])

  await userEvent.click(screen.getByRole('switch', { name: /flight sync/i }))

  expect(await screen.findByText(/could not/i)).toBeInTheDocument()
  expect(screen.getByRole('switch', { name: /flight sync/i })).toBeChecked()
})
```

- [ ] **Step 3: Run to verify they fail**

Run: `npm test -- JobsSection`
Expected: FAIL — the component does not exist.

- [ ] **Step 4: Implement, then run to verify they pass**

A section above Data sources on `/health`: one row per job with name, state, toggle, last outcome and run time, and — when off — who stopped it, why, and a note that manual runs still work. Switching **off** prompts for an optional reason before sending. Reuse `toneFor` from `src/pages/health/statusTone.ts` rather than a second colour map, and `apiErrorMessage` for failures, matching the sections beside it.

- [ ] **Step 5: Run the full gate**

```bash
npm test && npm run lint && npx tsc --noEmit
```
Expected: all pass. `lint` reports one pre-existing error in `src/components/ui/input.tsx`; it is identical on `main` and not yours.

- [ ] **Step 6: Verify in a browser**

jsdom has no layout engine, so the tests cannot show that the section renders correctly beside the others. Run the panel and look at `/health`: the Jobs section above Data sources, a toggle that works, and a disabled toggle that explains itself.

- [ ] **Step 7: Commit and open the panel PR**

```bash
git add -A
git commit -m "feat(health): switch the background jobs from the health page (#<panel issue>)"
git push -u origin feature/<n>-job-switches
gh pr create --base main --title "feat(health): switch the background jobs on and off (#<panel issue>)"
```

---

## Verification checklist

Before marking Phase 1 done:

- [ ] `dotnet test` passes in full
- [ ] `dotnet ef database update` applies on a clean database
- [ ] A switched-off loop runs no cycle — proven by removing the guard and watching the test fail (Tasks 5 and 6)
- [ ] A job switched off and back on resumes **without a restart**
- [ ] Config-disabled still terminates the service, heartbeat still `NeverRun`, and the switch is never read
- [ ] `PUT` against a config-disabled job returns 409 and writes no switch row
- [ ] Exactly one audit row per toggle, carrying the reason
- [ ] Manual sync and manual verification still work while the loop is off
- [ ] Verified by hand against a running API: a toggle takes effect within ~10s and `/admin/health` reports it
