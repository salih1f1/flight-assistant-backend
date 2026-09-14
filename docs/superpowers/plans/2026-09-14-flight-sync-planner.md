# Flight Sync Planner (step 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give each tracked flight its own sync mode and next-sync time, so the background sync calls AeroAPI only for flights that are due and answerable (closes #343, #344, #345).

**Architecture:** A pure `FlightSyncPlanner` in Application decides a flight's mode (Fast / Live / Schedule / Stopped / none) and next due time from its times, status and tracking. `SyncFlightStatusCommand` plans before and after each call and stores the result on `Flight`. The background service wakes every minute and sends the command only for tracked flights whose `NextSyncDueUtc` has passed, oldest first.

**Tech Stack:** .NET 10, EF Core + Npgsql, MediatR, xUnit, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`, Testcontainers PostgreSQL.

**Spec:** `docs/superpowers/specs/2026-09-14-flight-sync-schedule-design.md` — delivery step 1 only (§6). Issue #356.

**Amended 2026-09-15 (#364, Phase 1 item 1):** Tasks 1–5 were done before the amendment. Execution order from here: **Task 10** (rebase onto `development` now, not only in Task 9), **Task 11** (Fast and Live defaults lengthened to 5 and 30 min), then Tasks 6, 7, 8, 9. Task 7's #344 test and Task 8's CLAUDE.md row carry the new values.

## Global Constraints

- Work only in the worktree `/Users/salihalishov/Repositories/flight-assistant-backend-356`, branch `feature/356-flight-sync-planner`. Never touch the main checkout or other worktrees.
- Schedule-mode flights make **no** AeroAPI call in this step. They are re-planned and next due at the start of Live (`ScheduledDepartureUtc − StatusWindowFutureDays`).
- Both sync endpoints keep their current response `{ updated }`. No new DTO fields.
- Out of scope: `/schedules` check, writes to `ScheduleMissingSinceUtc`, `AdminFlightSyncResultDto`, admin DTO fields, #347, #303, admin panel.
- Column names follow the repo's existing EF convention (PascalCase, e.g. `NextSyncDueUtc`), not the spec's snake_case spelling. Table stays `flights`.
- `SyncMode` is stored as text, `varchar(16)` (`HasConversion<string>()`, the `AdminAuditLog.Action` precedent).
- Never run `dotnet ef database update` or SQL against the shared dev database. Never run the API with sync on.
- **EF migration:** until Task 9, keep a working migration named `AddFlightSyncSchedule` **uncommitted** (the Postgres test fixture runs `MigrateAsync`, so repository tests need it). Task 9 rebases, deletes it, and regenerates it.
- Do not hand-edit `spec/openapi.json` or the model snapshot.
- Tasks 3–8: the working migration files and `FlightAssistantDbContextModelSnapshot.cs` stay untracked/modified. Commit with explicit paths only; never `git add` them, `-A`, `.`, or the `Migrations` directory.
- Every commit message ends with a blank line and `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`. Commit only to `feature/356-flight-sync-planner`; never push to `development`.
- TDD: every test is seen failing first. Each task ends with a "prove" step: break the code on purpose, show the test failing, restore. After restoring from a `.bak`, `touch` the file so MSBuild recompiles.
- Tests with option values use **non-default** values, so a hardcoded constant can't pass (#308).

## Commands

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-356
dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj --filter "FullyQualifiedName~FlightTests"
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~FlightSyncPlannerTests"
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FlightRepositoryTests"   # needs Docker
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightStatusSyncBackgroundServiceTests"
# Working migration (uncommitted until Task 9):
dotnet ef migrations add AddFlightSyncSchedule --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
```

## File map

| File | Change |
|---|---|
| `FlightAssistant.Domain/Flights/Enums/FlightSyncMode.cs` | Create: `Fast, Live, Schedule, Stopped` |
| `FlightAssistant.Domain/Flights/Entities/Flight.cs` | 5 properties, `MarkSynced`, `ApplySyncPlan`, `ApplyStatusUpdate(..., nowUtc)` sets/clears `CancelledAtUtc` |
| `FlightAssistant.Application/Interfaces/ExternalServices/IFlightStatusWindowOptions.cs` | Create: `StatusWindowFutureDays` |
| `FlightAssistant.Application/FlightSync/IFlightSyncOptions.cs` | Create |
| `FlightAssistant.Application/FlightSync/FlightSyncPlanner.cs` | Create: `FlightSyncPlanner`, `FlightSyncPlan`, `FlightSyncPlanOptions` |
| `FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs` | `SyncTrigger`; plan before/after; due-only behaviour |
| `FlightAssistant.Application/Commands/Flights/TrackFlightCommand.cs` | Plan an unplanned flight, due now |
| `FlightAssistant.Application/Commands/Flights/UntrackFlightCommand.cs` | Last untrack clears the plan |
| `FlightAssistant.Application/Interfaces/Repositories/IFlightRepository.cs` | `GetDueTrackedFlightIdsAsync`, `IsFlightTrackedAsync`; remove `GetActiveTrackedFlightsAsync` |
| `FlightAssistant.Application/Administration/BrowsableTables.cs` | 5 columns in the `flights` entry only |
| `FlightAssistant.Infrastructure/Persistance/Configurations/Flights/FlightConfiguration.cs` | `SyncMode` conversion, `ix_flight_next_sync_due` |
| `FlightAssistant.Infrastructure/Persistance/Repositories/FlightRepository.cs` | New queries; drop `IOptions<AeroApiOptions>` ctor param |
| `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiOptions.cs` | Implements `IFlightStatusWindowOptions`; comment fix |
| `FlightAssistant.Infrastructure/DependencyInjection.cs` | `IFlightStatusWindowOptions` translation |
| `FlightAssistant.API/HostedServices/FlightSyncOptions.cs` | Create: options class, validated on start |
| `FlightAssistant.API/HostedServices/FlightSyncServiceCollectionExtensions.cs` | Create: `AddFlightSync()` |
| `FlightAssistant.API/HostedServices/FlightStatusSyncBackgroundService.cs` | Due-only loop, `RunOnceAsync` |
| `FlightAssistant.API/Program.cs`, `appsettings.json` | `AddFlightSync()`; `FlightSync` keys |
| `FlightAssistant.API/Endpoints/FlightEndpoints.cs`, `Admin/AdminFlightEndpoints.cs` | Pass `SyncTrigger` |
| Migration `AddFlightSyncSchedule` | Columns, index, backfill (Task 9) |
| Tests | Domain, planner, handlers, repository, background service, options, migration backfill |

---

### Task 1: Domain — sync fields on `Flight`

**Files:**
- Create: `FlightAssistant.Domain/Flights/Enums/FlightSyncMode.cs`
- Modify: `FlightAssistant.Domain/Flights/Entities/Flight.cs`
- Modify (compile fix only): `FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs` (pass `DateTime.UtcNow` as the new last argument; Task 4 replaces it)
- Test: `FlightAssistant.Domain.Tests/Flights/FlightTests.cs`

**Interfaces:**
- Produces: `enum FlightSyncMode { Fast, Live, Schedule, Stopped }` (namespace `FlightAssistant.Domain.Flights.Enums`); `Flight.LastSyncedUtc`, `NextSyncDueUtc`, `SyncMode`, `ScheduleMissingSinceUtc`, `CancelledAtUtc` (all nullable, private set); `Flight.CancelledStatusCode = "CAN"`; `void MarkSynced(DateTime syncedUtc)`; `void ApplySyncPlan(FlightSyncMode? mode, DateTime? nextSyncDueUtc)`; `void ApplyStatusUpdate(string statusCode, DateTime? estimatedDepartureUtc, DateTime? estimatedArrivalUtc, DateTime? actualDepartureUtc, DateTime? actualArrivalUtc, string? equipmentAircraftIcao, DateTime nowUtc)`.

- [ ] **Step 1: Write the failing tests** — add to `FlightTests` (and change the existing `ApplyStatusUpdate_UpdatesTimestamps` call to pass `DateTime.UtcNow` as the last argument):

```csharp
private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

private static Flight NewFlight() => Flight.Create(1, "TK1028", 1, 2, new DateOnly(2026, 9, 14),
    Now.AddHours(1), Now.AddHours(4));

[Fact]
public void ApplyStatusUpdate_Cancelled_SetsCancelledAtUtc()
{
    var flight = NewFlight();
    flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now);
    Assert.Equal(Now, flight.CancelledAtUtc);
}

[Fact]
public void ApplyStatusUpdate_CancelledAgain_KeepsTheFirstCancellationTime()
{
    // A later CAN update must not push the Stopped rule's 30-minute clock forward.
    var flight = NewFlight();
    flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now);
    flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now.AddMinutes(20));
    Assert.Equal(Now, flight.CancelledAtUtc);
}

[Fact]
public void ApplyStatusUpdate_OtherCodeAfterCancelled_ClearsCancelledAtUtc()
{
    var flight = NewFlight();
    flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now);
    flight.ApplyStatusUpdate("SCH", null, null, null, null, null, Now.AddMinutes(5));
    Assert.Null(flight.CancelledAtUtc);
}

[Fact]
public void MarkSynced_SetsLastSyncedUtc()
{
    var flight = NewFlight();
    flight.MarkSynced(Now);
    Assert.Equal(Now, flight.LastSyncedUtc);
}

[Fact]
public void ApplySyncPlan_SetsModeAndNextDue()
{
    var flight = NewFlight();
    flight.ApplySyncPlan(FlightSyncMode.Live, Now.AddMinutes(10));
    Assert.Equal(FlightSyncMode.Live, flight.SyncMode);
    Assert.Equal(Now.AddMinutes(10), flight.NextSyncDueUtc);
}

[Theory]
[InlineData(null)]
[InlineData(FlightSyncMode.Stopped)]
public void ApplySyncPlan_NotSyncedModeWithANextDueTime_Throws(FlightSyncMode? mode)
{
    var flight = NewFlight();
    Assert.Throws<ArgumentException>(() => flight.ApplySyncPlan(mode, Now));
}
```

Add `using FlightAssistant.Domain.Flights.Enums;` to the test file.

- [ ] **Step 2: Run, expect compile failure** (`FlightSyncMode`, `MarkSynced`, `ApplySyncPlan` don't exist).

Run: `dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj --filter "FullyQualifiedName~FlightTests"`

- [ ] **Step 3: Implement**

`FlightSyncMode.cs`:

```csharp
namespace FlightAssistant.Domain.Flights.Enums
{
    /// <summary>How often the status sync checks a tracked flight. See FlightSyncPlanner.</summary>
    public enum FlightSyncMode
    {
        Fast,
        Live,
        Schedule,
        Stopped
    }
}
```

In `Flight.cs`, after `UpdatedUtc`:

```csharp
        public const string CancelledStatusCode = "CAN";

        // --- Status sync schedule (#356). Written only by the sync, tracking and untracking.
        public DateTime? LastSyncedUtc { get; private set; }
        public DateTime? NextSyncDueUtc { get; private set; }
        public FlightSyncMode? SyncMode { get; private set; }
        public DateTime? ScheduleMissingSinceUtc { get; private set; }

        /// <summary>When a status update first reported <c>CAN</c>; cleared by any later other code.
        /// <c>Flight</c> doesn't store its current status, and the Stopped rule needs this.</summary>
        public DateTime? CancelledAtUtc { get; private set; }
```

Replace `ApplyStatusUpdate` with:

```csharp
        public void ApplyStatusUpdate(
            string statusCode,
            DateTime? estimatedDepartureUtc,
            DateTime? estimatedArrivalUtc,
            DateTime? actualDepartureUtc,
            DateTime? actualArrivalUtc,
            string? equipmentAircraftIcao,
            DateTime nowUtc)
        {
            EstimatedDepartureUtc = estimatedDepartureUtc;
            EstimatedArrivalUtc = estimatedArrivalUtc;
            ActualDepartureUtc = actualDepartureUtc;
            ActualArrivalUtc = actualArrivalUtc;
            if (equipmentAircraftIcao is not null)
                EquipmentAircraftIcao = equipmentAircraftIcao;
            CancelledAtUtc = statusCode == CancelledStatusCode ? CancelledAtUtc ?? nowUtc : null;
            UpdatedUtc = nowUtc;

            Raise(new FlightStatusChanged(Id, FlightNumber, ServiceDateUtc, statusCode, nowUtc));
        }

        /// <summary>Records a check against the provider, whether or not anything changed.</summary>
        public void MarkSynced(DateTime syncedUtc) => LastSyncedUtc = syncedUtc;

        /// <summary>
        /// Stores the planner's mode and next due time. Leaves <see cref="UpdatedUtc"/> alone: this
        /// changes every few minutes and isn't a change to the flight.
        /// </summary>
        public void ApplySyncPlan(FlightSyncMode? mode, DateTime? nextSyncDueUtc)
        {
            if (nextSyncDueUtc is not null && mode is null or FlightSyncMode.Stopped)
                throw new ArgumentException("A flight that isn't synced can't have a next sync time.", nameof(nextSyncDueUtc));

            SyncMode = mode;
            NextSyncDueUtc = nextSyncDueUtc;
        }
```

Add `using FlightAssistant.Domain.Flights.Enums;`. In `SyncFlightStatusCommand.cs`, add `DateTime.UtcNow` as the last `ApplyStatusUpdate` argument.

- [ ] **Step 4: Run, expect PASS.** Also `dotnet build` (whole solution, 0 errors).

- [ ] **Step 5: Prove** — `cp Flight.cs Flight.cs.bak`; change `CancelledAtUtc ?? nowUtc` to `nowUtc`; run: `ApplyStatusUpdate_CancelledAgain_KeepsTheFirstCancellationTime` FAILS. Restore `mv Flight.cs.bak Flight.cs && touch Flight.cs`; run again: PASS. Record the failing output for the PR.

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Domain FlightAssistant.Domain.Tests FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs
git commit -m "feat(domain): sync schedule fields on Flight (#356)"
```

---
### Task 2: Application — `FlightSyncPlanner`

**Files:**
- Create: `FlightAssistant.Application/Interfaces/ExternalServices/IFlightStatusWindowOptions.cs`
- Create: `FlightAssistant.Application/FlightSync/IFlightSyncOptions.cs`
- Create: `FlightAssistant.Application/FlightSync/FlightSyncPlanner.cs`
- Test: `FlightAssistant.Application.Tests/FlightSync/FlightSyncPlannerTests.cs`

**Interfaces:**
- Consumes: Task 1's `Flight` members and `FlightSyncMode`.
- Produces:
  - `interface IFlightStatusWindowOptions { int StatusWindowFutureDays { get; } }` (namespace `FlightAssistant.Application.Interfaces.ExternalServices`)
  - `interface IFlightSyncOptions { TimeSpan NearDepartureInterval; TimeSpan Interval; TimeSpan ScheduleCheckInterval; TimeSpan FastWindowBeforeDeparture; TimeSpan StopAfterArrival; TimeSpan StopWithoutArrivalAfter; }` (getters; namespace `FlightAssistant.Application.FlightSync`)
  - `sealed record FlightSyncPlanOptions(TimeSpan FastInterval, TimeSpan LiveInterval, TimeSpan ScheduleCheckInterval, TimeSpan FastWindowBeforeDeparture, TimeSpan LiveWindowBeforeDeparture, TimeSpan StopAfterArrival, TimeSpan StopWithoutArrivalAfter)` with `static FlightSyncPlanOptions From(IFlightSyncOptions sync, IFlightStatusWindowOptions window)`
  - `sealed record FlightSyncPlan(FlightSyncMode? Mode, DateTime? NextSyncDueUtc)`
  - `static class FlightSyncPlanner { FlightSyncPlan Plan(Flight flight, bool isTracked, DateTime nowUtc, FlightSyncPlanOptions options); DateTime LiveStartsAtUtc(Flight flight, FlightSyncPlanOptions options); }`

- [ ] **Step 1: Write the failing tests**

```csharp
using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;

namespace FlightAssistant.Application.Tests.FlightSync;

/// <summary>Spec §1 and §1.1. Every option is a non-default value, so a hardcoded 4 h / 2 days /
/// 30 min / 24 h / 2 min / 10 min / 1 day can't pass.</summary>
public class FlightSyncPlannerTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private static readonly FlightSyncPlanOptions Options = new(
        FastInterval: TimeSpan.FromMinutes(3),
        LiveInterval: TimeSpan.FromMinutes(7),
        ScheduleCheckInterval: TimeSpan.FromHours(6),
        FastWindowBeforeDeparture: TimeSpan.FromHours(3),
        LiveWindowBeforeDeparture: TimeSpan.FromDays(1),
        StopAfterArrival: TimeSpan.FromMinutes(20),
        StopWithoutArrivalAfter: TimeSpan.FromHours(12));

    private static Flight Departing(DateTime departureUtc, DateTime? arrivalUtc = null) =>
        Flight.Create(1, "TK1028", 10, 20, DateOnly.FromDateTime(departureUtc),
            departureUtc, arrivalUtc ?? departureUtc.AddHours(3));

    private static FlightSyncPlan Plan(Flight flight, bool isTracked = true) =>
        FlightSyncPlanner.Plan(flight, isTracked, Now, Options);

    // ── none ──
    [Fact]
    public void Untracked_HasNoModeAndNoNextSync()
    {
        Assert.Equal(new FlightSyncPlan(null, null), Plan(Departing(Now.AddHours(1)), isTracked: false));
    }

    // ── Stopped ──
    [Fact]
    public void ArrivedExactlyStopAfterArrivalAgo_IsStopped()
    {
        var flight = Departing(Now.AddHours(-4));
        flight.ApplyStatusUpdate("ARR", null, null, Now.AddHours(-4), Now - Options.StopAfterArrival, null, Now);
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Stopped, null), Plan(flight));
    }

    [Fact]
    public void ArrivedOneMinuteInsideStopAfterArrival_IsStillFast()
    {
        var flight = Departing(Now.AddHours(-4));
        flight.ApplyStatusUpdate("ARR", null, null, Now.AddHours(-4), Now - Options.StopAfterArrival + OneMinute, null, Now);
        Assert.Equal(FlightSyncMode.Fast, Plan(flight).Mode);
    }

    [Fact]
    public void CancelledExactlyStopAfterArrivalAgo_IsStopped()
    {
        var flight = Departing(Now.AddHours(1));
        flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now - Options.StopAfterArrival);
        Assert.Equal(FlightSyncMode.Stopped, Plan(flight).Mode);
    }

    [Fact]
    public void CancelledOneMinuteInsideStopAfterArrival_IsNotStopped()
    {
        var flight = Departing(Now.AddHours(1));
        flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now - Options.StopAfterArrival + OneMinute);
        Assert.Equal(FlightSyncMode.Fast, Plan(flight).Mode);
    }

    [Fact]
    public void NoArrivalReported_ScheduledArrivalExactlyStopWithoutArrivalAgo_IsStopped()
    {
        var arrival = Now - Options.StopWithoutArrivalAfter;
        Assert.Equal(FlightSyncMode.Stopped, Plan(Departing(arrival.AddHours(-3), arrival)).Mode);
    }

    [Fact]
    public void NoArrivalReported_ScheduledArrivalOneMinuteInside_IsNotStopped()
    {
        var arrival = Now - Options.StopWithoutArrivalAfter + OneMinute;
        Assert.Equal(FlightSyncMode.Fast, Plan(Departing(arrival.AddHours(-3), arrival)).Mode);
    }

    [Fact]
    public void NoArrivalReported_EstimatedArrivalWinsOverScheduled()
    {
        // Scheduled arrival is long past, but the estimate says it hasn't landed yet: keep syncing.
        var scheduledArrival = Now - Options.StopWithoutArrivalAfter - TimeSpan.FromHours(1);
        var flight = Departing(scheduledArrival.AddHours(-3), scheduledArrival);
        flight.ApplyStatusUpdate("DLY", null, Now - Options.StopWithoutArrivalAfter + OneMinute, null, null, null, Now);
        Assert.Equal(FlightSyncMode.Fast, Plan(flight).Mode);
    }

    [Fact]
    public void RecentActualArrival_WinsOverAScheduledArrivalLongAgo()
    {
        var scheduledArrival = Now - Options.StopWithoutArrivalAfter - TimeSpan.FromHours(1);
        var flight = Departing(scheduledArrival.AddHours(-3), scheduledArrival);
        flight.ApplyStatusUpdate("ARR", null, null, null, Now.AddMinutes(-5), null, Now);
        Assert.Equal(FlightSyncMode.Fast, Plan(flight).Mode);
    }

    // ── Fast ──
    [Fact]
    public void DepartingExactlyFastWindowAway_IsFast_NextSyncAfterFastInterval()
    {
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Fast, Now + Options.FastInterval),
            Plan(Departing(Now + Options.FastWindowBeforeDeparture)));
    }

    [Fact]
    public void DepartingOneMinuteOutsideFastWindow_IsLive()
    {
        Assert.Equal(FlightSyncMode.Live, Plan(Departing(Now + Options.FastWindowBeforeDeparture + OneMinute)).Mode);
    }

    [Fact]
    public void EstimatedDeparture_MovesTheFastWindow()
    {
        // Delayed: scheduled inside the window, estimated outside it.
        var delayed = Departing(Now.AddHours(1));
        delayed.ApplyStatusUpdate("DLY", Now + Options.FastWindowBeforeDeparture + OneMinute, null, null, null, null, Now);
        Assert.Equal(FlightSyncMode.Live, Plan(delayed).Mode);

        // Still waiting to leave long after the scheduled time: stays Fast.
        var late = Departing(Now.AddHours(-2));
        late.ApplyStatusUpdate("DLY", Now.AddHours(1), null, null, null, null, Now);
        Assert.Equal(FlightSyncMode.Fast, Plan(late).Mode);
    }

    // ── Live ──
    [Fact]
    public void ScheduledExactlyLiveWindowAway_IsLive()
    {
        Assert.Equal(FlightSyncMode.Live, Plan(Departing(Now + Options.LiveWindowBeforeDeparture)).Mode);
    }

    [Fact]
    public void ScheduledOneMinuteOutsideLiveWindow_IsSchedule()
    {
        Assert.Equal(FlightSyncMode.Schedule, Plan(Departing(Now + Options.LiveWindowBeforeDeparture + OneMinute)).Mode);
    }

    [Fact]
    public void LiveWindow_UsesScheduledDeparture_NotTheEstimate()
    {
        var flight = Departing(Now + Options.LiveWindowBeforeDeparture + OneMinute);
        flight.ApplyStatusUpdate("SCH", Now + Options.LiveWindowBeforeDeparture - OneMinute, null, null, null, null, Now);
        Assert.Equal(FlightSyncMode.Schedule, Plan(flight).Mode);
    }

    [Fact]
    public void Live_FarFromFast_NextSyncAfterLiveInterval()
    {
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Live, Now + Options.LiveInterval),
            Plan(Departing(Now.AddHours(10))));
    }

    [Fact]
    public void Live_NextSyncCappedAtTheStartOfFast()
    {
        var departure = Now + Options.FastWindowBeforeDeparture + TimeSpan.FromMinutes(2); // Fast starts in 2 min < LiveInterval
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Live, departure - Options.FastWindowBeforeDeparture),
            Plan(Departing(departure)));
    }

    // ── Schedule ──
    [Fact]
    public void Schedule_FarFromLive_NextSyncAfterScheduleCheckInterval()
    {
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Schedule, Now + Options.ScheduleCheckInterval),
            Plan(Departing(Now.AddDays(30))));
    }

    [Fact]
    public void Schedule_NextSyncCappedAtTheStartOfLive()
    {
        var departure = Now + Options.LiveWindowBeforeDeparture + TimeSpan.FromHours(1); // Live starts in 1 h < 6 h
        Assert.Equal(new FlightSyncPlan(FlightSyncMode.Schedule, departure - Options.LiveWindowBeforeDeparture),
            Plan(Departing(departure)));
    }

    [Fact]
    public void LiveStartsAtUtc_IsScheduledDepartureMinusTheLiveWindow()
    {
        var departure = Now.AddDays(5);
        Assert.Equal(departure - Options.LiveWindowBeforeDeparture, FlightSyncPlanner.LiveStartsAtUtc(Departing(departure), Options));
    }

    // ── Options mapping ──
    private sealed class SyncOptions : IFlightSyncOptions
    {
        public TimeSpan NearDepartureInterval => TimeSpan.FromMinutes(1);
        public TimeSpan Interval => TimeSpan.FromMinutes(2);
        public TimeSpan ScheduleCheckInterval => TimeSpan.FromMinutes(3);
        public TimeSpan FastWindowBeforeDeparture => TimeSpan.FromMinutes(4);
        public TimeSpan StopAfterArrival => TimeSpan.FromMinutes(5);
        public TimeSpan StopWithoutArrivalAfter => TimeSpan.FromMinutes(6);
    }

    private sealed class WindowOptions : IFlightStatusWindowOptions
    {
        public int StatusWindowFutureDays => 1;
    }

    [Fact]
    public void From_MapsEveryOption()
    {
        Assert.Equal(new FlightSyncPlanOptions(
                FastInterval: TimeSpan.FromMinutes(1),
                LiveInterval: TimeSpan.FromMinutes(2),
                ScheduleCheckInterval: TimeSpan.FromMinutes(3),
                FastWindowBeforeDeparture: TimeSpan.FromMinutes(4),
                LiveWindowBeforeDeparture: TimeSpan.FromDays(1),
                StopAfterArrival: TimeSpan.FromMinutes(5),
                StopWithoutArrivalAfter: TimeSpan.FromMinutes(6)),
            FlightSyncPlanOptions.From(new SyncOptions(), new WindowOptions()));
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~FlightSyncPlannerTests"`

- [ ] **Step 3: Implement**

`IFlightStatusWindowOptions.cs`:

```csharp
namespace FlightAssistant.Application.Interfaces.ExternalServices
{
    /// <summary>
    /// How far ahead the status provider can answer (AeroAPI's <c>end</c> bound), exposed to
    /// Application the way <see cref="IFlightSearchHorizonOptions"/> is. The planner starts Live
    /// polling this long before scheduled departure (#343).
    /// </summary>
    public interface IFlightStatusWindowOptions
    {
        int StatusWindowFutureDays { get; }
    }
}
```

`IFlightSyncOptions.cs`:

```csharp
namespace FlightAssistant.Application.FlightSync
{
    /// <summary>The <c>FlightSync</c> settings the planner needs (spec §2.7). Implemented by the
    /// API's <c>FlightSyncOptions</c>, which is bound and validated on start.</summary>
    public interface IFlightSyncOptions
    {
        /// <summary>Fast interval.</summary>
        TimeSpan NearDepartureInterval { get; }

        /// <summary>Live interval.</summary>
        TimeSpan Interval { get; }

        TimeSpan ScheduleCheckInterval { get; }
        TimeSpan FastWindowBeforeDeparture { get; }
        TimeSpan StopAfterArrival { get; }
        TimeSpan StopWithoutArrivalAfter { get; }
    }
}
```

`FlightSyncPlanner.cs`:

```csharp
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;

namespace FlightAssistant.Application.FlightSync
{
    public sealed record FlightSyncPlan(FlightSyncMode? Mode, DateTime? NextSyncDueUtc);

    public sealed record FlightSyncPlanOptions(
        TimeSpan FastInterval,
        TimeSpan LiveInterval,
        TimeSpan ScheduleCheckInterval,
        TimeSpan FastWindowBeforeDeparture,
        TimeSpan LiveWindowBeforeDeparture,
        TimeSpan StopAfterArrival,
        TimeSpan StopWithoutArrivalAfter)
    {
        public static FlightSyncPlanOptions From(IFlightSyncOptions sync, IFlightStatusWindowOptions window) => new(
            FastInterval: sync.NearDepartureInterval,
            LiveInterval: sync.Interval,
            ScheduleCheckInterval: sync.ScheduleCheckInterval,
            FastWindowBeforeDeparture: sync.FastWindowBeforeDeparture,
            LiveWindowBeforeDeparture: TimeSpan.FromDays(window.StatusWindowFutureDays),
            StopAfterArrival: sync.StopAfterArrival,
            StopWithoutArrivalAfter: sync.StopWithoutArrivalAfter);
    }

    /// <summary>
    /// Decides how often a flight is synced (spec §1). Rules, first match wins: untracked → none;
    /// Stopped; Fast; Live; Schedule. The next sync is capped at the moment the mode would change,
    /// so a stored mode is never more than one loop out of date (§1.1).
    /// </summary>
    public static class FlightSyncPlanner
    {
        public static FlightSyncPlan Plan(Flight flight, bool isTracked, DateTime nowUtc, FlightSyncPlanOptions options)
        {
            if (!isTracked)
                return new FlightSyncPlan(null, null);

            if (IsStopped(flight, nowUtc, options))
                return new FlightSyncPlan(FlightSyncMode.Stopped, null);

            var fastStartsAt = (flight.EstimatedDepartureUtc ?? flight.ScheduledDepartureUtc) - options.FastWindowBeforeDeparture;
            if (fastStartsAt <= nowUtc)
                return new FlightSyncPlan(FlightSyncMode.Fast, nowUtc + options.FastInterval);

            var liveStartsAt = LiveStartsAtUtc(flight, options);
            if (liveStartsAt <= nowUtc)
                return new FlightSyncPlan(FlightSyncMode.Live, Earliest(nowUtc + options.LiveInterval, fastStartsAt));

            return new FlightSyncPlan(FlightSyncMode.Schedule, Earliest(nowUtc + options.ScheduleCheckInterval, liveStartsAt));
        }

        /// <summary>When the status provider can first answer for the flight (#343). From the
        /// scheduled departure, not the service date, because the provider's bound is an exact time.</summary>
        public static DateTime LiveStartsAtUtc(Flight flight, FlightSyncPlanOptions options) =>
            flight.ScheduledDepartureUtc - options.LiveWindowBeforeDeparture;

        private static bool IsStopped(Flight flight, DateTime nowUtc, FlightSyncPlanOptions options)
        {
            // Keep syncing for a while after arrival or cancellation, so the final status, gate and
            // baggage claim are still recorded (#345).
            if (flight.ActualArrivalUtc is { } arrived)
                return arrived <= nowUtc - options.StopAfterArrival;

            if (flight.CancelledAtUtc is { } cancelled && cancelled <= nowUtc - options.StopAfterArrival)
                return true;

            // A flight that never reports an arrival.
            return (flight.EstimatedArrivalUtc ?? flight.ScheduledArrivalUtc) <= nowUtc - options.StopWithoutArrivalAfter;
        }

        private static DateTime Earliest(DateTime a, DateTime b) => a <= b ? a : b;
    }
}
```

Note on `IsStopped`: spec §1 row 2 is an OR of three rules. With an actual arrival present, the third rule doesn't apply ("no `ActualArrivalUtc` and …"), and the cancellation rule can't apply to a flight that landed. Returning from the first branch keeps `RecentActualArrival_WinsOverAScheduledArrivalLongAgo` true. The `IFlightStatusWindowOptions` using is only for `From`.

- [ ] **Step 4: Run, expect PASS (22 tests).**

- [ ] **Step 5: Prove** — break each in turn, run, see the named test fail, restore with `mv … .bak` and `touch`:
  1. `<= nowUtc - options.StopAfterArrival` → `<` in the actual-arrival branch: `ArrivedExactlyStopAfterArrivalAgo_IsStopped` fails.
  2. Replace `Earliest(nowUtc + options.LiveInterval, fastStartsAt)` with `nowUtc + options.LiveInterval`: `Live_NextSyncCappedAtTheStartOfFast` fails.
  3. Replace `flight.EstimatedDepartureUtc ?? flight.ScheduledDepartureUtc` with `flight.ScheduledDepartureUtc`: `EstimatedDeparture_MovesTheFastWindow` fails.
  4. Replace `options.FastWindowBeforeDeparture` with `TimeSpan.FromHours(4)`: fast-window tests fail.

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Application/FlightSync FlightAssistant.Application/Interfaces/ExternalServices/IFlightStatusWindowOptions.cs FlightAssistant.Application.Tests/FlightSync
git commit -m "feat: FlightSyncPlanner decides each flight's sync mode and next sync (#356)"
```

---
### Task 3: Infrastructure — columns, browsable columns, due query

**Files:**
- Modify: `FlightAssistant.Infrastructure/Persistance/Configurations/Flights/FlightConfiguration.cs`
- Modify: `FlightAssistant.Application/Administration/BrowsableTables.cs` (the `flights` entry only)
- Modify: `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiOptions.cs` (add `IFlightStatusWindowOptions` to the implemented interfaces)
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs`
- Modify: `FlightAssistant.Application/Interfaces/Repositories/IFlightRepository.cs`, `FlightAssistant.Infrastructure/Persistance/Repositories/FlightRepository.cs`
- Create (**uncommitted**): migration `AddFlightSyncSchedule`
- Test: `FlightAssistant.Infrastructure.Tests/Persistance/Repositories/FlightRepositoryTests.cs`, `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiDependencyInjectionTests.cs`

**Interfaces:**
- Consumes: Task 1 (`Flight` sync properties, `ApplySyncPlan`), Task 2 (`IFlightStatusWindowOptions`).
- Produces: `Task<IReadOnlyList<int>> IFlightRepository.GetDueTrackedFlightIdsAsync(CancellationToken ct = default)` (reads now from the repository's `TimeProvider`); `Task<bool> IFlightRepository.IsFlightTrackedAsync(int flightId, CancellationToken ct = default)`; `IFlightStatusWindowOptions` resolvable from DI. `GetActiveTrackedFlightsAsync` and the `IOptions<AeroApiOptions>` constructor parameter stay until Task 6.

**Red before you start:** since Task 1, `BrowsableTablesModelCompletenessTests` fails (five mapped `Flight` properties are neither exposed nor excluded) and every Postgres test fails (the model has changes no migration covers). Run both once and record the failures; they are this task's first red tests.

- [ ] **Step 1: Write the failing tests**

In `AeroApiDependencyInjectionTests`, give `BuildContainer` optional extra settings and add a test:

```csharp
    private static ServiceProvider BuildContainer(IDictionary<string, string?>? extraSettings = null)
    {
        var settings = new Dictionary<string, string?> { ["AeroApi:ApiKey"] = "dummy-test-key" };
        foreach (var (key, value) in extraSettings ?? new Dictionary<string, string?>())
            settings[key] = value;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        // ... rest of the method unchanged
```

```csharp
    [Fact]
    public void AddInfrastructure_ResolvesIFlightStatusWindowOptions_FromTheBoundAeroApiOptions()
    {
        // FlightSyncPlanner starts Live polling this many days before departure (#343). A
        // non-default value, so a translation that ignores configuration can't pass.
        using var provider = BuildContainer(new Dictionary<string, string?> { ["AeroApi:StatusWindowFutureDays"] = "1" });

        Assert.Equal(1, provider.GetRequiredService<IFlightStatusWindowOptions>().StatusWindowFutureDays);
    }
```

In `FlightRepositoryTests`, add `using FlightAssistant.Domain.Flights.Enums;` and:

```csharp
    private static readonly DateTime SyncNow = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    private static FakeTimeProvider ClockAt(DateTime utc) => new(new DateTimeOffset(utc));

    /// <summary>A saved flight with the given next sync time, tracked by one new traveler unless
    /// <paramref name="tracked"/> is false.</summary>
    private static Flight FlightDue(FlightAssistantDbContext db, string flightNumber, DateTime? nextSyncDueUtc, bool tracked = true)
    {
        var (airlineId, departureAirportId, arrivalAirportId) = EnsureReferenceData(db);
        var flight = Flight.Create(airlineId, flightNumber, departureAirportId, arrivalAirportId,
            new DateOnly(2026, 9, 14), SyncNow.AddHours(1), SyncNow.AddHours(3));
        if (nextSyncDueUtc is not null)
            flight.ApplySyncPlan(FlightSyncMode.Fast, nextSyncDueUtc);
        db.Flights.Add(flight);
        db.SaveChanges();

        if (tracked)
            AddTracker(db, flight);

        return flight;
    }

    private static void AddTracker(FlightAssistantDbContext db, Flight flight)
    {
        var travelerId = Guid.NewGuid();
        EnsureTraveler(db, travelerId);
        db.TravelerFlights.Add(TravelerFlight.Create(travelerId, flight.Id));
        db.SaveChanges();
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_ReturnsDueFlights_OldestDueFirst()
    {
        using var db = CreateContext();
        // Inserted out of due order, so only an ORDER BY can produce the expected order.
        var dueAnHourAgo = FlightDue(db, "TK1", SyncNow.AddHours(-1));
        var dueTenMinutesAgo = FlightDue(db, "TK2", SyncNow.AddMinutes(-10));
        var dueHalfAnHourAgo = FlightDue(db, "TK3", SyncNow.AddMinutes(-30));

        var ids = await Repository(db, ClockAt(SyncNow)).GetDueTrackedFlightIdsAsync();

        Assert.Equal(new[] { dueAnHourAgo.Id, dueHalfAnHourAgo.Id, dueTenMinutesAgo.Id }, ids);
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_IncludesAFlightDueExactlyNow_ExcludesOneDueAMinuteLater()
    {
        using var db = CreateContext();
        var dueNow = FlightDue(db, "TK1", SyncNow);
        FlightDue(db, "TK2", SyncNow.AddMinutes(1));

        var ids = await Repository(db, ClockAt(SyncNow)).GetDueTrackedFlightIdsAsync();

        Assert.Equal(new[] { dueNow.Id }, ids);
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_ExcludesAFlightWithNoNextSync()
    {
        using var db = CreateContext();
        FlightDue(db, "TK1", nextSyncDueUtc: null);

        Assert.Empty(await Repository(db, ClockAt(SyncNow)).GetDueTrackedFlightIdsAsync());
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_ExcludesAnUntrackedFlight_EvenWhenDue()
    {
        using var db = CreateContext();
        FlightDue(db, "TK1", SyncNow.AddHours(-1), tracked: false);

        Assert.Empty(await Repository(db, ClockAt(SyncNow)).GetDueTrackedFlightIdsAsync());
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_ReturnsAFlightOnce_WhenSeveralTravelersTrackIt()
    {
        using var db = CreateContext();
        var flight = FlightDue(db, "TK1", SyncNow.AddHours(-1));
        AddTracker(db, flight);

        Assert.Equal(new[] { flight.Id }, await Repository(db, ClockAt(SyncNow)).GetDueTrackedFlightIdsAsync());
    }

    [Fact]
    public async Task GetDueTrackedFlightIdsAsync_ReadsTheClockOnEveryCall()
    {
        using var db = CreateContext();
        var clock = ClockAt(SyncNow);
        var repository = Repository(db, clock);
        var flight = FlightDue(db, "TK1", SyncNow.AddMinutes(5));

        Assert.Empty(await repository.GetDueTrackedFlightIdsAsync());

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(new[] { flight.Id }, await repository.GetDueTrackedFlightIdsAsync());
    }

    [Fact]
    public async Task IsFlightTrackedAsync_TrueOnlyForATrackedFlight()
    {
        using var db = CreateContext();
        var tracked = FlightDue(db, "TK1", null);
        var untracked = FlightDue(db, "TK2", null, tracked: false);
        var repository = Repository(db, ClockAt(SyncNow));

        Assert.True(await repository.IsFlightTrackedAsync(tracked.Id));
        Assert.False(await repository.IsFlightTrackedAsync(untracked.Id));
    }
```

- [ ] **Step 2: Run, expect compile failure** (`GetDueTrackedFlightIdsAsync`, `IsFlightTrackedAsync`, `IFlightStatusWindowOptions` not implemented).

- [ ] **Step 3: Implement**

`FlightConfiguration.cs`, after the `ServiceDateUtc` line:

```csharp
            // Text, so the data browser shows "Live" rather than 1.
            b.Property(x => x.SyncMode).HasConversion<string>().HasMaxLength(16);
```

and with the other indexes:

```csharp
            b.HasIndex(x => x.NextSyncDueUtc).HasDatabaseName("ix_flight_next_sync_due");
```

`BrowsableTables.cs`, `flights` entry, after `nameof(Flight.UpdatedUtc),`:

```csharp
                nameof(Flight.LastSyncedUtc),
                nameof(Flight.NextSyncDueUtc),
                nameof(Flight.SyncMode),
                nameof(Flight.ScheduleMissingSinceUtc),
                nameof(Flight.CancelledAtUtc),
```

`AeroApiOptions.cs`: `public sealed class AeroApiOptions : IFlightSearchHorizonOptions, IAeroApiBudgetOptions, IFlightStatusWindowOptions`.

`DependencyInjection.cs`, after the `IAeroApiBudgetOptions` registration:

```csharp
            // Same translation (#356): FlightSyncPlanner starts Live polling StatusWindowFutureDays
            // before departure, the furthest ahead GET /flights/{ident} can answer.
            services.AddScoped<IFlightStatusWindowOptions>(sp =>
                sp.GetRequiredService<IOptions<AeroApiOptions>>().Value);
```

`IFlightRepository.cs`, beside `GetActiveTrackedFlightsAsync`:

```csharp
        /// <summary>
        /// Ids of tracked flights whose <see cref="Flight.NextSyncDueUtc"/> is now or earlier, oldest
        /// due first, for <c>FlightStatusSyncBackgroundService</c> (#356). "Now" comes from the
        /// repository's <see cref="TimeProvider"/> on every call.
        /// </summary>
        Task<IReadOnlyList<int>> GetDueTrackedFlightIdsAsync(CancellationToken ct = default);

        /// <summary>Whether any traveler tracks the flight.</summary>
        Task<bool> IsFlightTrackedAsync(int flightId, CancellationToken ct = default);
```

`FlightRepository.cs`:

```csharp
        public async Task<IReadOnlyList<int>> GetDueTrackedFlightIdsAsync(CancellationToken ct = default)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // Any(), not a join: a flight tracked by several travelers is synced once.
            return await _db.Flights
                .Where(f => f.NextSyncDueUtc != null &&
                            f.NextSyncDueUtc <= now &&
                            _db.TravelerFlights.Any(tf => tf.FlightId == f.Id))
                .OrderBy(f => f.NextSyncDueUtc)
                .ThenBy(f => f.Id)
                .Select(f => f.Id)
                .ToListAsync(ct);
        }

        public Task<bool> IsFlightTrackedAsync(int flightId, CancellationToken ct = default) =>
            _db.TravelerFlights.AnyAsync(tf => tf.FlightId == flightId, ct);
```

Generate the working migration (**do not `git add` it**). `migrations add` doesn't connect; the variable only satisfies the design-time factory:

```bash
CONNECTION_STRING="Host=localhost;Port=5432;Database=flightassistant;Username=fa;Password=fa" \
  dotnet ef migrations add AddFlightSyncSchedule --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
```

Check the generated `Up`: five nullable `AddColumn`s on `flights` (`SyncMode` as `character varying(16)`) and `CreateIndex ix_flight_next_sync_due`. Nothing else. If anything else appears, stop and report it.

- [ ] **Step 4: Run, expect PASS** — `FlightRepositoryTests`, `AeroApiDependencyInjectionTests`, `BrowsableTablesModelCompletenessTests`; then the full `FlightAssistant.Infrastructure.Tests` project.

- [ ] **Step 5: Prove**
  1. Remove `.OrderBy(f => f.NextSyncDueUtc).ThenBy(f => f.Id)`: `..._OldestDueFirst` fails.
  2. Change `<= now` to `< now`: `..._IncludesAFlightDueExactlyNow_...` fails.
  3. Replace the `Any()` condition with a `Join` on `TravelerFlights`: `..._ReturnsAFlightOnce_...` fails.
  4. Remove `nameof(Flight.SyncMode),` from `BrowsableTables`: `BrowsableTablesModelCompletenessTests` fails.
  Restore each (`mv … .bak`, `touch`).

- [ ] **Step 6: Commit** (explicit paths; the migration stays untracked)

```bash
git add FlightAssistant.Infrastructure/Persistance/Configurations/Flights/FlightConfiguration.cs \
  FlightAssistant.Application/Administration/BrowsableTables.cs \
  FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiOptions.cs \
  FlightAssistant.Infrastructure/DependencyInjection.cs \
  FlightAssistant.Application/Interfaces/Repositories/IFlightRepository.cs \
  FlightAssistant.Infrastructure/Persistance/Repositories/FlightRepository.cs \
  FlightAssistant.Infrastructure.Tests/Persistance/Repositories/FlightRepositoryTests.cs \
  FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiDependencyInjectionTests.cs
git commit -m "feat: flight sync columns and the due-flights query (#356)"
```

---
### Task 4: API — `FlightSyncOptions`, validated on start

**Files:**
- Create: `FlightAssistant.API/HostedServices/FlightSyncOptions.cs`
- Create: `FlightAssistant.API/HostedServices/FlightSyncServiceCollectionExtensions.cs`
- Modify: `FlightAssistant.API/Program.cs` (replace the `AddSingleton<FlightSyncHeartbeat>()` and `AddHostedService<FlightStatusSyncBackgroundService>()` lines with `builder.Services.AddFlightSync();`)
- Modify: `FlightAssistant.API/appsettings.json` (the `FlightSync` section only)
- Test: `FlightAssistant.API.Tests/HostedServices/FlightSyncOptionsTests.cs`

**Interfaces:**
- Consumes: Task 2's `IFlightSyncOptions`.
- Produces: `FlightSyncOptions : IFlightSyncOptions` with `Enabled`, `LoopInterval` and the six interface properties (settable); `FlightSyncOptions.SectionName = "FlightSync"`; `IServiceCollection AddFlightSync(this IServiceCollection services)` registering the options (bound, data annotations, on start), `IFlightSyncOptions` as a singleton translation, `FlightSyncHeartbeat` and the hosted service. Registered before Task 5, so no handler has an unresolvable dependency.

- [ ] **Step 1: Write the failing tests**

```csharp
using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.FlightSync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.Tests.HostedServices;

public class FlightSyncOptionsTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddFlightSync();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        using var provider = Build(new());
        var options = provider.GetRequiredService<IOptions<FlightSyncOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), options.LoopInterval);
        Assert.Equal(TimeSpan.FromMinutes(2), options.NearDepartureInterval);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Interval);
        Assert.Equal(TimeSpan.FromDays(1), options.ScheduleCheckInterval);
        Assert.Equal(TimeSpan.FromHours(4), options.FastWindowBeforeDeparture);
        Assert.Equal(TimeSpan.FromMinutes(30), options.StopAfterArrival);
        Assert.Equal(TimeSpan.FromDays(1), options.StopWithoutArrivalAfter);
    }

    [Fact]
    public void IFlightSyncOptions_ReflectsTheBoundSection()
    {
        using var provider = Build(new()
        {
            ["FlightSync:NearDepartureInterval"] = "00:03:00",
            ["FlightSync:Interval"] = "00:07:00",
            ["FlightSync:ScheduleCheckInterval"] = "06:00:00",
            ["FlightSync:FastWindowBeforeDeparture"] = "03:00:00",
            ["FlightSync:StopAfterArrival"] = "00:20:00",
            ["FlightSync:StopWithoutArrivalAfter"] = "12:00:00",
        });
        var options = provider.GetRequiredService<IFlightSyncOptions>();

        Assert.Equal(TimeSpan.FromMinutes(3), options.NearDepartureInterval);
        Assert.Equal(TimeSpan.FromMinutes(7), options.Interval);
        Assert.Equal(TimeSpan.FromHours(6), options.ScheduleCheckInterval);
        Assert.Equal(TimeSpan.FromHours(3), options.FastWindowBeforeDeparture);
        Assert.Equal(TimeSpan.FromMinutes(20), options.StopAfterArrival);
        Assert.Equal(TimeSpan.FromHours(12), options.StopWithoutArrivalAfter);
    }

    [Theory]
    [InlineData("LoopInterval", "00:00:00")]
    [InlineData("NearDepartureInterval", "00:00:00")]
    [InlineData("Interval", "00:00:00")]
    [InlineData("ScheduleCheckInterval", "00:00:00")]
    [InlineData("FastWindowBeforeDeparture", "00:00:00")]
    [InlineData("StopAfterArrival", "-00:01:00")]
    [InlineData("StopWithoutArrivalAfter", "00:00:00")]
    public void AnOutOfRangeValue_FailsOnStart(string key, string value)
    {
        using var provider = Build(new() { [$"FlightSync:{key}"] = value });

        // IStartupValidator is what the host runs at startup; it exists only because of ValidateOnStart.
        var ex = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains(key, ex.Message);
    }
}
```

- [ ] **Step 2: Run, expect compile failure** (`AddFlightSync`, `FlightSyncOptions` don't exist).

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightSyncOptionsTests"`

- [ ] **Step 3: Implement**

`FlightSyncOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using FlightAssistant.Application.FlightSync;

namespace FlightAssistant.API.HostedServices
{
    /// <summary>
    /// The <c>FlightSync</c> section (spec §2.7). Bound and validated on start by
    /// <see cref="FlightSyncServiceCollectionExtensions.AddFlightSync"/>. Application reads it
    /// through <see cref="IFlightSyncOptions"/>.
    /// </summary>
    public sealed class FlightSyncOptions : IFlightSyncOptions
    {
        public const string SectionName = "FlightSync";

        public bool Enabled { get; set; } = true;

        /// <summary>How often the background service looks for due flights.</summary>
        [Range(typeof(TimeSpan), "00:00:10", "00:10:00")]
        public TimeSpan LoopInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>The Fast interval.</summary>
        [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
        public TimeSpan NearDepartureInterval { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>The Live interval.</summary>
        [Range(typeof(TimeSpan), "00:01:00", "06:00:00")]
        public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);

        [Range(typeof(TimeSpan), "01:00:00", "7.00:00:00")]
        public TimeSpan ScheduleCheckInterval { get; set; } = TimeSpan.FromDays(1);

        [Range(typeof(TimeSpan), "00:30:00", "1.00:00:00")]
        public TimeSpan FastWindowBeforeDeparture { get; set; } = TimeSpan.FromHours(4);

        [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
        public TimeSpan StopAfterArrival { get; set; } = TimeSpan.FromMinutes(30);

        [Range(typeof(TimeSpan), "01:00:00", "7.00:00:00")]
        public TimeSpan StopWithoutArrivalAfter { get; set; } = TimeSpan.FromDays(1);
    }
}
```

`FlightSyncServiceCollectionExtensions.cs`:

```csharp
using FlightAssistant.Application.FlightSync;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.HostedServices
{
    public static class FlightSyncServiceCollectionExtensions
    {
        /// <summary>The status sync: its options (validated on start), their Application-layer view,
        /// the heartbeat and the background service.</summary>
        public static IServiceCollection AddFlightSync(this IServiceCollection services)
        {
            services.AddOptions<FlightSyncOptions>()
                .BindConfiguration(FlightSyncOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Application can't reference this API-layer class (the IFlightSearchHorizonOptions pattern).
            services.AddSingleton<IFlightSyncOptions>(sp =>
                sp.GetRequiredService<IOptions<FlightSyncOptions>>().Value);

            services.AddSingleton<FlightSyncHeartbeat>();
            services.AddHostedService<FlightStatusSyncBackgroundService>();
            return services;
        }
    }
}
```

`appsettings.json`, `FlightSync` section (keep anything another branch adds elsewhere in the file):

```json
  "FlightSync": {
    "Enabled": true,
    "LoopInterval": "00:01:00",
    "Interval": "00:10:00",
    "NearDepartureInterval": "00:02:00",
    "ScheduleCheckInterval": "1.00:00:00",
    "FastWindowBeforeDeparture": "04:00:00",
    "StopAfterArrival": "00:30:00",
    "StopWithoutArrivalAfter": "1.00:00:00"
  },
```

- [ ] **Step 4: Run, expect PASS**; then the whole `FlightAssistant.API.Tests` project (the test host now builds with `AddFlightSync`).

- [ ] **Step 5: Prove**
  1. Remove `.ValidateDataAnnotations()`: every `AnOutOfRangeValue_FailsOnStart` case fails.
  2. Remove `.ValidateOnStart()`: the same cases fail (no `IStartupValidator`, a different exception type).
  3. Make the translation return `new FlightSyncOptions()`: `IFlightSyncOptions_ReflectsTheBoundSection` fails.
  Restore each (`mv … .bak`, `touch`).

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.API/HostedServices/FlightSyncOptions.cs FlightAssistant.API/HostedServices/FlightSyncServiceCollectionExtensions.cs \
  FlightAssistant.API/Program.cs FlightAssistant.API/appsettings.json FlightAssistant.API.Tests/HostedServices/FlightSyncOptionsTests.cs
git commit -m "feat: FlightSync options validated on start (#356)"
```

---
### Task 5: Application — `SyncFlightStatusCommand` plans each sync

**Files:**
- Modify: `FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs`
- Modify (pass the trigger, nothing else): `FlightAssistant.API/Endpoints/FlightEndpoints.cs` (`SyncTrigger.Traveler`), `FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs` (`SyncTrigger.Admin`), `FlightAssistant.API/HostedServices/FlightStatusSyncBackgroundService.cs` (`SyncTrigger.Scheduled`; rewritten in Task 7), `FlightAssistant.Application.Tests/Validators/ValidatorEdgeCaseTests.cs` (add a trigger argument where the command is constructed)
- Test: `FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs` (rewrite)

**Interfaces:**
- Consumes: Task 1 (`MarkSynced`, `ApplySyncPlan`, `ApplyStatusUpdate(..., nowUtc)`), Task 2 (`FlightSyncPlanner`, `FlightSyncPlanOptions`, `IFlightSyncOptions`, `IFlightStatusWindowOptions`), Task 3 (`IsFlightTrackedAsync`).
- Produces: `enum SyncTrigger { Scheduled, Admin, Traveler }`; `record SyncFlightStatusCommand(Guid? TravelerId, int FlightId, SyncTrigger Trigger) : IRequest<bool>` (still true only when a new status event is recorded); handler constructor `(IFlightRepository flights, IFlightStatusProvider provider, IFlightSyncOptions syncOptions, IFlightStatusWindowOptions windowOptions, TimeProvider timeProvider, ILogger<SyncFlightStatusCommandHandler> logger)`.

**Behaviour (spec §2.3, step 1):**

| Planned mode before the call | Trigger | AeroAPI call | Stored afterwards | Returns |
|---|---|---|---|---|
| none (untracked) | any | no | mode null, next null | false |
| Stopped | Scheduled | no | Stopped, next null | false |
| Stopped | Admin / Traveler | yes | `LastSyncedUtc = now`, re-planned | true if new event |
| Fast / Live | any | yes | `LastSyncedUtc = now`, re-planned | true if new event |
| Schedule | any | **no** (step 2 adds the check) | Schedule, next = `LiveStartsAtUtc` | false |

`LastSyncedUtc` is set for any call that returned, including an empty result or a duplicate. If the provider throws, or `UNK` is missing, the flight is not modified and nothing is saved. All entity changes happen after every step that can throw, because the background service may share a `DbContext` across flights.

- [ ] **Step 1: Write the failing tests** — replace the test file's fixtures and tests with:

```csharp
using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Flights;

public class SyncFlightStatusCommandHandlerTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly IFlightStatusProvider _provider = Substitute.For<IFlightStatusProvider>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly SyncFlightStatusCommandHandler _handler;

    // Non-default values, so the handler can't pass with the planner's defaults hardcoded.
    private sealed class SyncOptions : IFlightSyncOptions
    {
        public TimeSpan NearDepartureInterval => TimeSpan.FromMinutes(3);
        public TimeSpan Interval => TimeSpan.FromMinutes(7);
        public TimeSpan ScheduleCheckInterval => TimeSpan.FromHours(6);
        public TimeSpan FastWindowBeforeDeparture => TimeSpan.FromHours(3);
        public TimeSpan StopAfterArrival => TimeSpan.FromMinutes(20);
        public TimeSpan StopWithoutArrivalAfter => TimeSpan.FromHours(12);
    }

    private sealed class WindowOptions : IFlightStatusWindowOptions
    {
        public int StatusWindowFutureDays => 1;
    }

    // (Keep the existing CapturingLogger<T> helper class unchanged.)

    private static Flight Departing(DateTime departureUtc) =>
        Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(departureUtc), departureUtc, departureUtc.AddHours(8));

    private static FlightStatusResult Status(string code = "DEP", DateTime? effectiveAtUtc = null, DateTime? estimatedDepartureUtc = null) => new(
        StatusCode: code,
        EffectiveAtUtc: effectiveAtUtc ?? Now,
        EstimatedDepartureUtc: estimatedDepartureUtc,
        EstimatedArrivalUtc: null,
        ActualDepartureUtc: null,
        ActualArrivalUtc: null,
        Gate: "A1",
        Terminal: "T5",
        BaggageClaim: null,
        EquipmentIcao: "B789",
        ExternalId: "ext-1",
        RawPayloadJson: null);

    private SyncFlightStatusCommandHandler Handler(ILogger<SyncFlightStatusCommandHandler>? logger = null) =>
        new(_flights, _provider, new SyncOptions(), new WindowOptions(), _clock,
            logger ?? NullLogger<SyncFlightStatusCommandHandler>.Instance);

    public SyncFlightStatusCommandHandlerTests()
    {
        _handler = Handler();
        _provider.ProviderName.Returns("TestProvider");
        _flights.IsFlightTrackedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _flights.GetFlightStatusIdByCodeAsync("DEP", Arg.Any<CancellationToken>()).Returns(2);
        _flights.GetFlightStatusIdByCodeAsync("CAN", Arg.Any<CancellationToken>()).Returns(5);
        _flights.GetFlightStatusIdByCodeAsync("SCH", Arg.Any<CancellationToken>()).Returns(1);
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
    }

    private Flight Given(Flight flight)
    {
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(flight);
        return flight;
    }

    private Task<bool> Sync(SyncTrigger trigger = SyncTrigger.Scheduled, Guid? travelerId = null) =>
        _handler.Handle(new SyncFlightStatusCommand(travelerId, 1, trigger), CancellationToken.None);

    // ── Fast / Live ──

    [Fact]
    public async Task Fast_NewStatus_RecordsEvent_MarksSynced_NextSyncAfterTheFastInterval()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<CancellationToken>()).Returns(Status());

        Assert.True(await Sync());

        await _flights.Received(1).AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(Now, flight.LastSyncedUtc);
        Assert.Equal(FlightSyncMode.Fast, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(3), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task Live_NextSyncAfterTheLiveInterval()
    {
        var flight = Given(Departing(Now.AddHours(10)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync();

        Assert.Equal(FlightSyncMode.Live, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(7), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task ProviderReturnsNothing_ReturnsFalse_ButRecordsTheSyncAndSaves()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).ReturnsNull();

        Assert.False(await Sync());

        Assert.Equal(Now, flight.LastSyncedUtc);
        Assert.Equal(Now.AddMinutes(3), flight.NextSyncDueUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DuplicateStatus_ReturnsFalse_AddsNoEvent_ButRecordsTheSync()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(Status());
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        Assert.False(await Sync());

        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        Assert.Equal(Now, flight.LastSyncedUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StatusUpdateMovesTheDeparture_NextSyncIsPlannedFromTheNewTimes()
    {
        // Live before the call; the estimate puts departure inside the 3 h Fast window.
        var flight = Given(Departing(Now.AddHours(10)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Status("DLY", estimatedDepartureUtc: Now.AddHours(2)));
        _flights.GetFlightStatusIdByCodeAsync("DLY", Arg.Any<CancellationToken>()).Returns(4);

        await Sync();

        Assert.Equal(FlightSyncMode.Fast, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(3), flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task Cancelled_SetsCancelledAtUtc_AndALaterOtherStatusClearsIt()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(Status("CAN"));

        await Sync();
        Assert.Equal(Now, flight.CancelledAtUtc);

        _clock.Advance(TimeSpan.FromMinutes(3));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Status("SCH", effectiveAtUtc: Now.AddMinutes(3)));

        await Sync();
        Assert.Null(flight.CancelledAtUtc);
    }

    // ── none / Stopped / Schedule ──

    [Fact]
    public async Task Untracked_MakesNoCall_ClearsThePlan()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        flight.ApplySyncPlan(FlightSyncMode.Fast, Now);
        _flights.IsFlightTrackedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        Assert.False(await Sync(SyncTrigger.Admin));

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        Assert.Null(flight.SyncMode);
        Assert.Null(flight.NextSyncDueUtc);
        Assert.Null(flight.LastSyncedUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static Flight ArrivedAnHourAgo()
    {
        var flight = Departing(Now.AddHours(-9));
        flight.ApplyStatusUpdate("ARR", null, null, Now.AddHours(-9), Now.AddHours(-1), null, Now.AddHours(-1));
        flight.ApplySyncPlan(FlightSyncMode.Fast, Now);
        return flight;
    }

    [Fact]
    public async Task Stopped_ScheduledTrigger_MakesNoCall_StoresStopped()
    {
        var flight = Given(ArrivedAnHourAgo());

        Assert.False(await Sync(SyncTrigger.Scheduled));

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        Assert.Equal(FlightSyncMode.Stopped, flight.SyncMode);
        Assert.Null(flight.NextSyncDueUtc);
        Assert.Null(flight.LastSyncedUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(SyncTrigger.Admin)]
    [InlineData(SyncTrigger.Traveler)]
    public async Task Stopped_AskedForByAPerson_StillCallsTheProvider(SyncTrigger trigger)
    {
        var flight = Given(ArrivedAnHourAgo());
        Guid? travelerId = trigger == SyncTrigger.Traveler ? Guid.NewGuid() : null;
        if (travelerId is { } id)
            _flights.GetTravelerFlightAsync(id, 1, Arg.Any<CancellationToken>()).Returns(TravelerFlight.Create(id, 1));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(trigger, travelerId);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<CancellationToken>());
        Assert.Equal(Now, flight.LastSyncedUtc);
        Assert.Equal(FlightSyncMode.Stopped, flight.SyncMode);
        Assert.Null(flight.NextSyncDueUtc);
    }

    [Theory]
    [InlineData(SyncTrigger.Scheduled)]
    [InlineData(SyncTrigger.Admin)]
    public async Task Schedule_MakesNoCall_NextSyncAtTheStartOfLive(SyncTrigger trigger)
    {
        var departure = Now.AddDays(5);
        var flight = Given(Departing(departure));

        Assert.False(await Sync(trigger));

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        Assert.Equal(FlightSyncMode.Schedule, flight.SyncMode);
        Assert.Equal(departure.AddDays(-1), flight.NextSyncDueUtc); // StatusWindowFutureDays = 1
        Assert.Null(flight.LastSyncedUtc);
    }

    // ── Failures ──

    [Fact]
    public async Task ProviderThrows_PropagatesAndLeavesTheFlightUntouched()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        flight.ApplySyncPlan(FlightSyncMode.Fast, Now);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns<FlightStatusResult?>(_ => throw new DomainException("AeroAPI unavailable", 502));

        await Assert.ThrowsAsync<DomainException>(() => Sync());

        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Null(flight.LastSyncedUtc);
        Assert.Equal(Now, flight.NextSyncDueUtc); // retried on the next loop
    }

    [Fact]
    public async Task FlightNotFound_Throws404()
    {
        _flights.GetFlightByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(new SyncFlightStatusCommand(null, 99, SyncTrigger.Admin), CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task WithTravelerId_NotTracking_Throws403()
    {
        var travelerId = Guid.NewGuid();
        _flights.GetTravelerFlightAsync(travelerId, 1, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() => Sync(SyncTrigger.Traveler, travelerId));

        Assert.Equal(403, ex.StatusCode);
        await _flights.DidNotReceive().GetFlightByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
```

Keep the two existing unrecognised-code tests and `TwoDistinctStatuses_BothStored`, adapted: flight from `Given(Departing(Now.AddHours(1)))`, `Status(...)` instead of `TestStatus`, `Handler(logger)` instead of `new SyncFlightStatusCommandHandler(_flights, _provider, logger)`, and `Sync()` for the command. In `UnrecognisedStatusCode_Throws_WhenUnkItselfIsNotSeeded`, also assert `Assert.Null(flight.LastSyncedUtc)`.

- [ ] **Step 2: Run, expect compile failure** (3-argument command, new constructor).

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests"`

- [ ] **Step 3: Implement** — replace the command, record and handler in `SyncFlightStatusCommand.cs` (the validator stays):

```csharp
using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.Commands.Flights
{
    /// <summary>Who asked for a sync. Only the background loop skips a Stopped flight.</summary>
    public enum SyncTrigger
    {
        Scheduled,
        Admin,
        Traveler
    }

    /// <summary>
    /// Syncs one flight when its sync mode calls for it, then stores when it's next due (#356).
    /// </summary>
    /// <param name="TravelerId">When set, enforces that the traveler is tracking the flight. Pass null for admin/background callers.</param>
    public record SyncFlightStatusCommand(Guid? TravelerId, int FlightId, SyncTrigger Trigger) : IRequest<bool>; // true if a new event was recorded

    public sealed class SyncFlightStatusCommandHandler : IRequestHandler<SyncFlightStatusCommand, bool>
    {
        // (Keep the UnknownStatusCode constant and its comment unchanged.)
        private const string UnknownStatusCode = "UNK";

        private readonly IFlightRepository _flights;
        private readonly IFlightStatusProvider _provider;
        private readonly IFlightSyncOptions _syncOptions;
        private readonly IFlightStatusWindowOptions _windowOptions;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<SyncFlightStatusCommandHandler> _logger;

        public SyncFlightStatusCommandHandler(
            IFlightRepository flights,
            IFlightStatusProvider provider,
            IFlightSyncOptions syncOptions,
            IFlightStatusWindowOptions windowOptions,
            TimeProvider timeProvider,
            ILogger<SyncFlightStatusCommandHandler> logger)
        {
            _flights = flights;
            _provider = provider;
            _syncOptions = syncOptions;
            _windowOptions = windowOptions;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task<bool> Handle(SyncFlightStatusCommand cmd, CancellationToken ct)
        {
            if (cmd.TravelerId is { } travelerId)
            {
                var link = await _flights.GetTravelerFlightAsync(travelerId, cmd.FlightId, ct);
                if (link is null)
                    throw new DomainException("Not tracking this flight.", 403);
            }

            var flight = await _flights.GetFlightByIdAsync(cmd.FlightId, ct)
                ?? throw new DomainException($"Flight {cmd.FlightId} not found.", 404);

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var options = FlightSyncPlanOptions.From(_syncOptions, _windowOptions);
            var isTracked = cmd.TravelerId is not null || await _flights.IsFlightTrackedAsync(flight.Id, ct);
            var plan = FlightSyncPlanner.Plan(flight, isTracked, now, options);

            switch (plan.Mode)
            {
                case null:
                    flight.ApplySyncPlan(null, null);
                    await _flights.SaveChangesAsync(ct);
                    return false;

                // The loop never calls for a finished flight (#345); a person asking still gets a call.
                case FlightSyncMode.Stopped when cmd.Trigger == SyncTrigger.Scheduled:
                    flight.ApplySyncPlan(FlightSyncMode.Stopped, null);
                    await _flights.SaveChangesAsync(ct);
                    return false;

                // AeroAPI can't answer before Live starts (#343). Step 2 of #349 adds a daily
                // /schedules check here; until then there is nothing to call.
                case FlightSyncMode.Schedule:
                    flight.ApplySyncPlan(FlightSyncMode.Schedule, FlightSyncPlanner.LiveStartsAtUtc(flight, options));
                    await _flights.SaveChangesAsync(ct);
                    return false;
            }

            var recorded = await ApplyLiveStatusAsync(flight, now, ct);

            // Any call that returned counts as a sync, even an empty or duplicate one. Re-plan from
            // the times the call may just have changed.
            flight.MarkSynced(now);
            var next = FlightSyncPlanner.Plan(flight, isTracked, now, options);
            flight.ApplySyncPlan(next.Mode, next.NextSyncDueUtc);

            await _flights.SaveChangesAsync(ct);
            return recorded;
        }

        /// <summary>
        /// Fetches the live status and, if it's new, records it. Changes the flight only after every
        /// step that can throw, so a failure leaves it untouched for the next attempt.
        /// </summary>
        private async Task<bool> ApplyLiveStatusAsync(Flight flight, DateTime now, CancellationToken ct)
        {
            var result = await _provider.GetStatusAsync(flight.FlightNumber, flight.ServiceDateUtc, ct);
            if (result is null) return false;

            // (Move the existing status-id resolution block here unchanged: resolvedStatusCode,
            // GetFlightStatusIdByCodeAsync, the UNK fallback with its warning and its throw.)

            var dedupHash = FlightStatusEvent.ComputeDedupHash(flight.Id, statusId.Value, result.EffectiveAtUtc, _provider.ProviderName, result.ExternalId);
            if (await _flights.FlightStatusEventExistsAsync(dedupHash, ct)) return false;

            var evt = FlightStatusEvent.Create(
                flight.Id,
                statusId.Value,
                result.EffectiveAtUtc,
                _provider.ProviderName,
                result.ExternalId,
                result.Gate,
                result.Terminal,
                result.BaggageClaim,
                rawPayloadJson: result.RawPayloadJson);

            // ApplyStatusUpdate raises FlightStatusChanged, which the DomainEventDispatchInterceptor
            // publishes after SaveChanges succeeds.
            flight.ApplyStatusUpdate(
                resolvedStatusCode,
                result.EstimatedDepartureUtc,
                result.EstimatedArrivalUtc,
                result.ActualDepartureUtc,
                result.ActualArrivalUtc,
                result.EquipmentIcao,
                now);

            await _flights.AddFlightStatusEventAsync(evt, ct);
            return true;
        }
    }
```

Then update the three API call sites and `ValidatorEdgeCaseTests` to pass a trigger (`SyncTrigger.Traveler` public, `SyncTrigger.Admin` admin, `SyncTrigger.Scheduled` background service, any value in validator tests). Response bodies stay `Results.Ok(new { updated })`.

- [ ] **Step 4: Run, expect PASS**; then `dotnet build` and the whole `FlightAssistant.Application.Tests` project.

- [ ] **Step 5: Prove**
  1. Delete the `case FlightSyncMode.Stopped when …` block: `Stopped_ScheduledTrigger_MakesNoCall_StoresStopped` fails.
  2. Remove `when cmd.Trigger == SyncTrigger.Scheduled`: `Stopped_AskedForByAPerson_StillCallsTheProvider` fails.
  3. Move `flight.MarkSynced(now);` to after `if (result is null) return false;` inside `ApplyLiveStatusAsync`: `ProviderReturnsNothing_…` fails.
  4. Re-plan with `plan` (the pre-call plan) instead of `next`: `StatusUpdateMovesTheDeparture_…` fails.
  5. Move `flight.MarkSynced(now);` to before the `ApplyLiveStatusAsync` call: `ProviderThrows_PropagatesAndLeavesTheFlightUntouched` fails.
  Restore each (`mv … .bak`, `touch`).

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs \
  FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs \
  FlightAssistant.Application.Tests/Validators/ValidatorEdgeCaseTests.cs \
  FlightAssistant.API/Endpoints/FlightEndpoints.cs FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs \
  FlightAssistant.API/HostedServices/FlightStatusSyncBackgroundService.cs
git commit -m "feat: sync a flight by its planned mode, with a SyncTrigger (#356)"
```

---
### Task 6: Application — tracking plans a flight, the last untrack clears it

**Files:**
- Modify: `FlightAssistant.Application/Commands/Flights/TrackFlightCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Flights/UntrackFlightCommand.cs`
- Test: `FlightAssistant.Application.Tests/Commands/Flights/TrackFlightCommandHandlerTests.cs`, `UntrackFlightCommandHandlerTests.cs`

**Interfaces:**
- Consumes: Task 1 (`ApplySyncPlan`, `MarkSynced`), Task 2 (`FlightSyncPlanner`, `FlightSyncPlanOptions`, options interfaces).
- Produces: `TrackFlightCommandHandler(IFlightRepository flights, IRefDataRepository refData, IFlightSyncOptions syncOptions, IFlightStatusWindowOptions windowOptions, TimeProvider timeProvider)`. `UntrackFlightCommandHandler` constructor unchanged.

**Behaviour (spec §2.5):**
- Track, new tracking row, flight has no `SyncMode`: plan it with `isTracked: true`; store the mode with `NextSyncDueUtc = now` (null when the plan is Stopped, which can't carry a due time). Both `POST /flights` and `POST /admin/travelers/{id}/flights` use this handler.
- Track when the flight already has a mode, or the traveler already tracks it: the plan is untouched.
- Untrack of the last tracker: `ApplySyncPlan(null, null)`; `LastSyncedUtc` is kept. Other trackers remaining: untouched.
- Known gap, not in this step: deleting a traveler cascades their `traveler_flights` rows without clearing plans. The loop only picks tracked flights, so it costs no calls; a stale `SyncMode` stays visible in the grid. Mention it in the PR.

- [ ] **Step 1: Write the failing tests**

`TrackFlightCommandHandlerTests` — construct the handler with the new dependencies and add:

```csharp
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));

    private sealed class SyncOptions : IFlightSyncOptions
    {
        public TimeSpan NearDepartureInterval => TimeSpan.FromMinutes(3);
        public TimeSpan Interval => TimeSpan.FromMinutes(7);
        public TimeSpan ScheduleCheckInterval => TimeSpan.FromHours(6);
        public TimeSpan FastWindowBeforeDeparture => TimeSpan.FromHours(3);
        public TimeSpan StopAfterArrival => TimeSpan.FromMinutes(20);
        public TimeSpan StopWithoutArrivalAfter => TimeSpan.FromHours(12);
    }

    private sealed class WindowOptions : IFlightStatusWindowOptions
    {
        public int StatusWindowFutureDays => 1;
    }

    // In the constructor:
    //   _handler = new TrackFlightCommandHandler(_flights, _refData, new SyncOptions(), new WindowOptions(), _clock);

    private TrackFlightCommand CommandDeparting(DateTime departureUtc) => DefaultCmd with
    {
        ServiceDate = DateOnly.FromDateTime(departureUtc),
        ScheduledDepartureUtc = departureUtc,
        ScheduledArrivalUtc = departureUtc.AddHours(8),
    };

    [Theory]
    [InlineData(1, FlightSyncMode.Fast)]       // departs in 1 h, inside the 3 h Fast window
    [InlineData(10, FlightSyncMode.Live)]      // within 1 day
    [InlineData(24 * 30, FlightSyncMode.Schedule)]
    public async Task FirstTracker_PlansTheFlight_AndMakesItDueNow(int hoursToDeparture, FlightSyncMode expected)
    {
        Flight? flight = null;
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>()).Returns(ci => flight = ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await _handler.Handle(CommandDeparting(Now.AddHours(hoursToDeparture)), CancellationToken.None);

        Assert.Equal(expected, flight!.SyncMode);
        Assert.Equal(Now, flight.NextSyncDueUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FirstTracker_OfAFinishedFlight_IsStopped_WithNoNextSync()
    {
        Flight? flight = null;
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>()).Returns(ci => flight = ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>()).ReturnsNull();

        // Arrived (by schedule) 2 days ago, well past StopWithoutArrivalAfter.
        await _handler.Handle(CommandDeparting(Now.AddDays(-2).AddHours(-8)), CancellationToken.None);

        Assert.Equal(FlightSyncMode.Stopped, flight!.SyncMode);
        Assert.Null(flight.NextSyncDueUtc);
    }

    [Fact]
    public async Task FlightAlreadyPlanned_KeepsItsPlan()
    {
        var existing = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(1), Now.AddHours(9));
        existing.ApplySyncPlan(FlightSyncMode.Live, Now.AddMinutes(5));
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>()).Returns(existing);
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, existing.Id, Arg.Any<CancellationToken>()).ReturnsNull();

        await _handler.Handle(CommandDeparting(Now.AddHours(1)), CancellationToken.None);

        Assert.Equal(FlightSyncMode.Live, existing.SyncMode);
        Assert.Equal(Now.AddMinutes(5), existing.NextSyncDueUtc);
    }

    [Fact]
    public async Task AlreadyTracking_DoesNotPlan()
    {
        var existing = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(1), Now.AddHours(9));
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>()).Returns(existing);
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, existing.Id, Arg.Any<CancellationToken>())
            .Returns(TravelerFlight.Create(DefaultCmd.TravelerId, existing.Id));

        await _handler.Handle(CommandDeparting(Now.AddHours(1)), CancellationToken.None);

        Assert.Null(existing.SyncMode);
    }
```

Add usings: `FlightAssistant.Application.FlightSync`, `FlightAssistant.Application.Interfaces.ExternalServices`, `FlightAssistant.Domain.Flights.Enums`, `Microsoft.Extensions.Time.Testing`.

`UntrackFlightCommandHandlerTests` — add (usings: `FlightAssistant.Domain.Flights.Enums`):

```csharp
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    private static Flight PlannedFlight()
    {
        var flight = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(10), Now.AddHours(18));
        flight.MarkSynced(Now.AddMinutes(-5));
        flight.ApplySyncPlan(FlightSyncMode.Live, Now.AddMinutes(2));
        return flight;
    }

    [Fact]
    public async Task LastTracker_ClearsThePlan_KeepsLastSynced()
    {
        var travelerId = Guid.NewGuid();
        var tf = TravelerFlight.Create(travelerId, 1);
        var flight = PlannedFlight();
        _flights.GetTravelerFlightAsync(travelerId, 1, Arg.Any<CancellationToken>()).Returns(tf);
        // Still returned: the removal isn't saved yet.
        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TravelerFlight> { tf });
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(flight);

        await _handler.Handle(new UntrackFlightCommand(travelerId, 1), CancellationToken.None);

        Assert.Null(flight.SyncMode);
        Assert.Null(flight.NextSyncDueUtc);
        Assert.Equal(Now.AddMinutes(-5), flight.LastSyncedUtc);
        await _flights.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OtherTrackersRemain_KeepsThePlan()
    {
        var travelerId = Guid.NewGuid();
        var tf = TravelerFlight.Create(travelerId, 1);
        var flight = PlannedFlight();
        _flights.GetTravelerFlightAsync(travelerId, 1, Arg.Any<CancellationToken>()).Returns(tf);
        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tf, TravelerFlight.Create(Guid.NewGuid(), 1) });
        _flights.GetFlightByIdAsync(1, Arg.Any<CancellationToken>()).Returns(flight);

        await _handler.Handle(new UntrackFlightCommand(travelerId, 1), CancellationToken.None);

        Assert.Equal(FlightSyncMode.Live, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(2), flight.NextSyncDueUtc);
    }
```

In the existing `Handle_ExistingTracking_RemovesAndSaves`, stub `GetTrackingsByFlightIdAsync(flightId, …)` to return `new List<TravelerFlight> { tf }`.

- [ ] **Step 2: Run, expect compile failure (Track) and assertion failures (Untrack).**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~TrackFlightCommandHandlerTests"` (matches both classes)

- [ ] **Step 3: Implement**

`TrackFlightCommand.cs` — new constructor fields (`IFlightSyncOptions _syncOptions`, `IFlightStatusWindowOptions _windowOptions`, `TimeProvider _timeProvider`), and replace the `if (existing is null)` block:

```csharp
            if (existing is null)
            {
                var tf = TravelerFlight.Create(cmd.TravelerId, flight.Id);
                await _flights.AddTravelerFlightAsync(tf, ct);

                // First time anyone tracks it (or the first since the last untrack): plan it and make it
                // due now, so the sync picks it up within a loop (#356, spec §2.5).
                if (flight.SyncMode is null)
                {
                    var now = _timeProvider.GetUtcNow().UtcDateTime;
                    var plan = FlightSyncPlanner.Plan(flight, isTracked: true, now,
                        FlightSyncPlanOptions.From(_syncOptions, _windowOptions));
                    flight.ApplySyncPlan(plan.Mode, plan.Mode == FlightSyncMode.Stopped ? null : now);
                }

                await _flights.SaveChangesAsync(ct);
            }
```

Usings: `FlightAssistant.Application.FlightSync`, `FlightAssistant.Application.Interfaces.ExternalServices`, `FlightAssistant.Domain.Flights.Enums`.

`UntrackFlightCommand.cs` — replace the body after the null check:

```csharp
            await _flights.RemoveTravelerFlightAsync(tf, ct);

            // Last tracker gone: nothing syncs the flight any more. LastSyncedUtc is kept (spec §2.5).
            // The unsaved removal can still come back from the query, so ignore it by id.
            var trackings = await _flights.GetTrackingsByFlightIdAsync(cmd.FlightId, ct);
            if (trackings.All(t => t.Id == tf.Id))
            {
                var flight = await _flights.GetFlightByIdAsync(cmd.FlightId, ct);
                flight?.ApplySyncPlan(null, null);
            }

            await _flights.SaveChangesAsync(ct);
```

- [ ] **Step 4: Run, expect PASS**; then the whole `FlightAssistant.Application.Tests` project.

- [ ] **Step 5: Prove**
  1. Track: remove `if (flight.SyncMode is null)` (always re-plan): `FlightAlreadyPlanned_KeepsItsPlan` fails.
  2. Track: pass `plan.NextSyncDueUtc` instead of `now`: `FirstTracker_PlansTheFlight_AndMakesItDueNow` fails.
  3. Untrack: change the condition to `trackings.Count == 0`: `LastTracker_ClearsThePlan_KeepsLastSynced` fails.
  4. Untrack: change it to `true`: `OtherTrackersRemain_KeepsThePlan` fails.
  Restore each (`mv … .bak`, `touch`).

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Application/Commands/Flights/TrackFlightCommand.cs FlightAssistant.Application/Commands/Flights/UntrackFlightCommand.cs \
  FlightAssistant.Application.Tests/Commands/Flights/TrackFlightCommandHandlerTests.cs FlightAssistant.Application.Tests/Commands/Flights/UntrackFlightCommandHandlerTests.cs
git commit -m "feat: tracking plans a flight's sync, the last untrack clears it (#356)"
```

---
### Task 7: API — the loop syncs only due flights

**Files:**
- Modify: `FlightAssistant.API/HostedServices/FlightStatusSyncBackgroundService.cs` (rewrite)
- Modify: `FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj` (add `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.9.0" />`)
- Modify: `FlightAssistant.Application/Interfaces/Repositories/IFlightRepository.cs`, `FlightAssistant.Infrastructure/Persistance/Repositories/FlightRepository.cs` (remove `GetActiveTrackedFlightsAsync`, the `IOptions<AeroApiOptions>` constructor parameter, `_statusWindowFutureDays`, and now-unused usings)
- Modify: every `new FlightRepository(` in `FlightAssistant.Infrastructure.Tests` (drop the options argument); `FlightRepositoryTests` (delete the `GetActiveTrackedFlightsAsync_*` tests, simplify `Repository(...)`, update the class summary to describe the due query)
- Test: `FlightAssistant.API.Tests/HostedServices/FlightStatusSyncBackgroundServiceTests.cs` (rewrite; the `IsActiveForFastPoll` tests go with the method)

**Interfaces:**
- Consumes: Task 3 (`GetDueTrackedFlightIdsAsync`), Task 4 (`FlightSyncOptions`), Task 5 (`SyncFlightStatusCommand`, `SyncTrigger`, handler constructor), Task 2 (planner, used by the handler).
- Produces: `FlightStatusSyncBackgroundService(IServiceScopeFactory scopeFactory, IOptions<FlightSyncOptions> options, TimeProvider timeProvider, ILogger<FlightStatusSyncBackgroundService> log, FlightSyncHeartbeat heartbeat)`; `public Task<FlightSyncRunResult> RunOnceAsync(CancellationToken ct)`; `public sealed record FlightSyncRunResult(int Processed, int Failed)`. `FlightRepository(FlightAssistantDbContext db, TimeProvider timeProvider)`.

**Behaviour (spec §2.2):** a run starts immediately, then on every `LoopInterval` tick (`PeriodicTimer` on the injected `TimeProvider`; a run longer than the interval is followed straight away by the next). Each run loads due ids once, then sends `SyncFlightStatusCommand(null, id, SyncTrigger.Scheduled)` for each in **its own DI scope**, counting failures and continuing. Heartbeat `NextRunDueUtc` = run start + `LoopInterval`. `Enabled = false` returns at once and leaves the heartbeat `NeverRun`.

- [ ] **Step 1: Write the failing tests** — replace the test file:

```csharp
using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.API.Tests.HostedServices;

public class FlightStatusSyncBackgroundServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly FlightSyncHeartbeat _heartbeat = new();
    private int _mediatorScopes;

    private FlightStatusSyncBackgroundService Service(FlightSyncOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _flights);
        // Counts how many scopes resolved a mediator: one per flight.
        services.AddScoped(_ => { _mediatorScopes++; return _mediator; });

        return new FlightStatusSyncBackgroundService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options ?? new FlightSyncOptions()),
            _clock,
            NullLogger<FlightStatusSyncBackgroundService>.Instance,
            _heartbeat);
    }

    private void DueIds(params int[] ids) =>
        _flights.GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>()).Returns(ids.ToList());

    private List<SyncFlightStatusCommand> Sent() => _mediator.ReceivedCalls()
        .Select(c => c.GetArguments()[0]).OfType<SyncFlightStatusCommand>().ToList();

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met within 5 s.");
            await Task.Delay(10);
        }
    }

    // ── RunOnceAsync ──

    [Fact]
    public async Task RunOnce_SendsAScheduledSyncForEachDueFlight_InTheRepositorysOrder()
    {
        DueIds(3, 1, 2);

        var result = await Service().RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { 3, 1, 2 }, Sent().Select(c => c.FlightId));
        Assert.All(Sent(), c =>
        {
            Assert.Equal(SyncTrigger.Scheduled, c.Trigger);
            Assert.Null(c.TravelerId);
        });
        Assert.Equal(new FlightSyncRunResult(3, 0), result);
    }

    [Fact]
    public async Task RunOnce_NoDueFlights_SendsNothing()
    {
        DueIds();

        Assert.Equal(new FlightSyncRunResult(0, 0), await Service().RunOnceAsync(CancellationToken.None));
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task RunOnce_AFailingFlight_IsCounted_AndTheRestStillSync()
    {
        DueIds(1, 2, 3);
        _mediator.Send(Arg.Is<SyncFlightStatusCommand>(c => c.FlightId == 1), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("boom"));

        var result = await Service().RunOnceAsync(CancellationToken.None);

        Assert.Equal(new FlightSyncRunResult(3, 1), result);
        Assert.Equal(new[] { 1, 2, 3 }, Sent().Select(c => c.FlightId));
    }

    [Fact]
    public async Task RunOnce_EachFlightSyncsInItsOwnScope()
    {
        // A shared DbContext would let one flight's failed, half-applied changes be saved by the next.
        DueIds(1, 2, 3);

        await Service().RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, _mediatorScopes);
    }

    // ── ExecuteAsync ──

    [Fact]
    public async Task Disabled_NeverLooksForFlights_AndTheHeartbeatStaysNeverRun()
    {
        var service = Service(new FlightSyncOptions { Enabled = false });

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        await _flights.DidNotReceive().GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>());
        Assert.Equal(FlightSyncOutcome.NeverRun, _heartbeat.Current.LastOutcome);
    }

    [Fact]
    public async Task Enabled_RunsAtOnce_ThenOnEveryLoopInterval()
    {
        DueIds();
        var service = Service(new FlightSyncOptions { LoopInterval = TimeSpan.FromSeconds(30) }); // non-default

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => _heartbeat.Current.LastOutcome == FlightSyncOutcome.Success);
            Assert.Equal(Now.AddSeconds(30), _heartbeat.Current.NextRunDueUtc);
            await _flights.Received(1).GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>());

            _clock.Advance(TimeSpan.FromSeconds(29));
            await Task.Delay(100);
            await _flights.Received(1).GetDueTrackedFlightIdsAsync(Arg.Any<CancellationToken>());

            _clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntil(() => _flights.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IFlightRepository.GetDueTrackedFlightIdsAsync)) == 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ── #344 end to end: real handler and planner, fake clock and repository ──

    private static Flight WithId(Flight flight, int id)
    {
        typeof(BaseIntEntity).GetProperty(nameof(BaseIntEntity.Id))!.SetValue(flight, id);
        return flight;
    }

    [Fact]
    public async Task OverTenMinutes_AFlightNearDeparture_DoesNotSpeedUpTheOthers()
    {
        // Non-default intervals (#308): Fast every 3 min, Live every 20 min; Fast window stays 4 h.
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
        provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var window = Substitute.For<IFlightStatusWindowOptions>();
        window.StatusWindowFutureDays.Returns(2);
        var handler = new SyncFlightStatusCommandHandler(_flights, provider, options, window, _clock,
            NullLogger<SyncFlightStatusCommandHandler>.Instance);
        _mediator.Send(Arg.Any<SyncFlightStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => handler.Handle(ci.ArgAt<SyncFlightStatusCommand>(0), CancellationToken.None).GetAwaiter().GetResult());

        var service = Service(options);
        for (var minute = 0; minute < 10; minute++)
        {
            await service.RunOnceAsync(CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        await provider.Received(4).GetStatusAsync("TK1", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()); // minutes 0, 3, 6, 9
        await provider.Received(1).GetStatusAsync("TK2", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()); // minute 0 only
        await provider.DidNotReceive().GetStatusAsync("TK3", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        Assert.Single(Sent(), c => c.FlightId == 3); // planned once, next due when Live starts
    }
}
```

If `_mediator.Send(Arg.Any<SyncFlightStatusCommand>(), …)` doesn't bind to MediatR's generic `Send<TResponse>(IRequest<TResponse>, …)`, use `_mediator.Send(Arg.Any<IRequest<bool>>(), Arg.Any<CancellationToken>())` and cast `ci[0]`. Same for the failing-flight test.

- [ ] **Step 2: Run, expect compile failure** (new constructor, `RunOnceAsync`, `FlightSyncRunResult`).

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightStatusSyncBackgroundServiceTests"`

- [ ] **Step 3: Implement** — replace `FlightStatusSyncBackgroundService.cs`:

```csharp
using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.HostedServices
{
    /// <summary>
    /// Syncs tracked flights whose <c>NextSyncDueUtc</c> has passed, oldest due first (#356, spec
    /// §2.2). Each flight's own sync mode sets when it's next due, so a flight near departure no
    /// longer speeds up every other flight (#344), and a finished or not-yet-answerable flight makes
    /// no call (#343, #345).
    /// </summary>
    public class FlightStatusSyncBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly FlightSyncOptions _options;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<FlightStatusSyncBackgroundService> _log;
        private readonly FlightSyncHeartbeat _heartbeat;

        public FlightStatusSyncBackgroundService(
            IServiceScopeFactory scopeFactory,
            IOptions<FlightSyncOptions> options,
            TimeProvider timeProvider,
            ILogger<FlightStatusSyncBackgroundService> log,
            FlightSyncHeartbeat heartbeat)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _timeProvider = timeProvider;
            _log = log;
            _heartbeat = heartbeat;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _log.LogInformation("FlightStatusSyncBackgroundService is disabled via config.");
                // Heartbeat is left untouched: it must stay NeverRun so the admin health endpoint
                // can render "disabled" rather than "broken".
                return;
            }

            // PeriodicTimer doesn't queue missed ticks: a run longer than the interval is followed
            // straight away by the next one. Due flights are taken oldest first, so none is skipped.
            using var timer = new PeriodicTimer(_options.LoopInterval, _timeProvider);
            try
            {
                do
                {
                    await RunAndRecordAsync(stoppingToken);
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Clean shutdown, not a failed run — heartbeat is left as-is.
            }
        }

        private async Task RunAndRecordAsync(CancellationToken ct)
        {
            _heartbeat.MarkRunStarted();
            var nextRunDueUtc = _timeProvider.GetUtcNow().UtcDateTime + _options.LoopInterval;

            try
            {
                var result = await RunOnceAsync(ct);
                _heartbeat.MarkRunSucceeded(result.Processed, result.Failed, nextRunDueUtc);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Flight status sync run failed. Retrying in {Interval}.", _options.LoopInterval);
                _heartbeat.MarkRunFailed(ex, nextRunDueUtc);
            }
        }

        /// <summary>
        /// One pass over the due flights. Each flight syncs in its own DI scope, so a flight that
        /// fails part-way can't have its changes saved by the next flight's <c>SaveChanges</c>.
        /// </summary>
        public async Task<FlightSyncRunResult> RunOnceAsync(CancellationToken ct)
        {
            IReadOnlyList<int> flightIds;
            using (var scope = _scopeFactory.CreateScope())
            {
                flightIds = await scope.ServiceProvider.GetRequiredService<IFlightRepository>()
                    .GetDueTrackedFlightIdsAsync(ct);
            }

            var failed = 0;
            foreach (var flightId in flightIds)
            {
                using var scope = _scopeFactory.CreateScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<IMediator>()
                        .Send(new SyncFlightStatusCommand(null, flightId, SyncTrigger.Scheduled), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Its NextSyncDueUtc is unchanged, so the next loop retries it.
                    _log.LogError(ex, "Failed to sync flight {FlightId}", flightId);
                    failed++;
                }
            }

            return new FlightSyncRunResult(flightIds.Count, failed);
        }
    }

    /// <summary>How many due flights a run sent, and how many of those threw.</summary>
    public sealed record FlightSyncRunResult(int Processed, int Failed);
}
```

Remove the old repository method and constructor parameter:

```bash
perl -0pi -e 's/new FlightRepository\(\s*(\w+),\s*TimeProvider\.System,\s*Options\.Create\(new AeroApiOptions \{ ApiKey = "dummy" \}\)\)/new FlightRepository($1, TimeProvider.System)/g' \
  FlightAssistant.Infrastructure.Tests/Persistance/Repositories/AdminDrilldownRepositoryTests.cs \
  FlightAssistant.Infrastructure.Tests/Persistance/Repositories/PagedRepositoryArgumentOrderTests.cs
grep -rn "new FlightRepository(" --include='*.cs' . | grep -v -e /bin/ -e /obj/
```

The grep must show no `AeroApiOptions` argument left. In `FlightRepositoryTests`, `Repository` becomes `private static FlightRepository Repository(FlightAssistantDbContext db, TimeProvider clock) => new(db, clock);`, and `TrackedFlight` goes if nothing uses it. In `PagedRepositoryArgumentOrderTests`, drop the comment that says only `GetActiveTrackedFlightsAsync` reads TimeProvider/AeroApiOptions. Remove unused `using` lines only after the build confirms they're unused.

- [ ] **Step 4: Run, expect PASS**; then `dotnet build` (0 errors) and all four test projects (`dotnet test`).

- [ ] **Step 5: Prove**
  1. Hoist `using var scope = _scopeFactory.CreateScope();` above the `foreach` and reuse it: `RunOnce_EachFlightSyncsInItsOwnScope` fails.
  2. In `SyncFlightStatusCommand.cs`, store `now + options.FastInterval` as the next due for every mode after a call (the #344 behaviour): `OverTenMinutes_AFlightNearDeparture_DoesNotSpeedUpTheOthers` fails (TK2 called 4 times).
  3. Replace `_options.LoopInterval` in the `PeriodicTimer` with `TimeSpan.FromMinutes(1)`: `Enabled_RunsAtOnce_ThenOnEveryLoopInterval` fails.
  4. Remove the `if (!_options.Enabled)` block: `Disabled_…` fails.
  Restore each (`mv … .bak`, `touch`).

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.API/HostedServices/FlightStatusSyncBackgroundService.cs FlightAssistant.API.Tests \
  FlightAssistant.Application/Interfaces/Repositories/IFlightRepository.cs \
  FlightAssistant.Infrastructure/Persistance/Repositories/FlightRepository.cs \
  FlightAssistant.Infrastructure.Tests/Persistance/Repositories
git commit -m "feat: the sync loop syncs only due flights, oldest first (#356)"
```

---
### Task 8: Endpoint smoke tests, stale comments, CLAUDE.md

**Files:**
- Test: `FlightAssistant.API.Tests/Integration/AdminFlightEndpointTests.cs`, `FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs`
- Modify (comments only): `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiOptions.cs` (`StatusWindowFutureDays` summary), `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightStatusProvider.cs` (`BuildUrl` summary)
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: everything above, through the real test host (`FlightAssistantWebApplicationFactory`, InMemory database, hosted services removed).
- Produces: nothing new. Both sync responses stay `{ "updated": bool }`.

- [ ] **Step 1: Write the endpoint tests** (they should pass straight away if Tasks 4–7 wired DI correctly; Step 3 proves they can fail)

`AdminFlightEndpointTests` (add `using System.Text.Json;`):

```csharp
    // ── Sync (#356): response shape unchanged; the real host resolves the new handler dependencies ──

    [Fact]
    public async Task PostSync_Admin_UntrackedFlight_Returns200_WithOnlyUpdatedFalse()
    {
        // Untracked, so the handler plans "none" and makes no provider call.
        var flight = await SeedFlightAsync($"TK{Marker()}");

        var response = await factory.CreateAdminClient().PostAsync($"admin/flights/{flight.Id}/sync", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("updated", Assert.Single(body.EnumerateObject()).Name);
        Assert.Equal(JsonValueKind.False, body.GetProperty("updated").ValueKind);
    }
```

`FlightEndpointTests` (add `using System.Text.Json;`, `using FlightAssistant.Domain.Flights.Entities;`, `using FlightAssistant.Domain.Flights.Enums;`, `using FlightAssistant.Infrastructure.Persistance;`, `using Microsoft.EntityFrameworkCore;`):

```csharp
    [Fact]
    public async Task PostSync_TrackingTraveler_FlightDaysAway_Returns200WithUpdatedFalse_AndPlansTheFlight()
    {
        var userId = Guid.NewGuid();
        var client = factory.CreateUserClient(userId.ToString());
        await client.PostAsJsonAsync("travelers", new { email = $"{userId}@example.com", displayName = "Test", countryCode = (string?)null });

        // 30 days out: Schedule mode, so no provider call in this step.
        var departure = DateTime.UtcNow.Date.AddDays(30).AddHours(9);
        var flight = Flight.Create(1, $"TK{Random.Shared.Next(1000, 9999)}", 10, 20, DateOnly.FromDateTime(departure), departure, departure.AddHours(3));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            db.Flights.Add(flight);
            await db.SaveChangesAsync();
            db.TravelerFlights.Add(TravelerFlight.Create(userId, flight.Id));
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync($"flights/{flight.Id}/sync", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("updated", Assert.Single(body.EnumerateObject()).Name);
        Assert.Equal(JsonValueKind.False, body.GetProperty("updated").ValueKind);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            var saved = await db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
            Assert.Equal(FlightSyncMode.Schedule, saved.SyncMode);
            // AeroApi:StatusWindowFutureDays defaults to 2: proves the host's IFlightStatusWindowOptions reaches the handler.
            Assert.Equal(departure.AddDays(-2), saved.NextSyncDueUtc);
        }
    }
```

- [ ] **Step 2: Run, expect PASS.** If either returns 500, read the logged exception: an unresolved dependency means Task 4's or Task 3's registration is missing.

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~PostSync"`

- [ ] **Step 3: Prove**
  1. In `FlightSyncServiceCollectionExtensions`, delete the `IFlightSyncOptions` registration: both tests fail (500).
  2. In `Infrastructure/DependencyInjection.cs`, delete the `IFlightStatusWindowOptions` registration: both fail.
  3. In `AdminFlightEndpoints`, return `Results.Ok(new { updated, mode = "x" })`: the admin test fails.
  Restore each (`mv … .bak`, `touch`).

- [ ] **Step 4: Check how the data grid shows `SyncMode` (investigation, no commit)**

The browser returns raw values, and no JSON enum converter is configured, so `SyncMode` may serialize as a number (`AdminAuditLog.Action` would too). Add a temporary test to `AdminFlightEndpointTests`: seed a flight, `ApplySyncPlan(FlightSyncMode.Live, DateTime.UtcNow)` before saving, `GET admin/data/flights?page=1&pageSize=200`, find the row by `id`, and print `row.GetProperty("syncMode")`. Run it, note whether it's `"Live"` or `1`, then delete the test. **Do not change the data browser.** Report the result in the PR description and to the user; if it's a number, the fix (enum names for every enum column, which also changes `action` in the audit log grid) is the user's call.

- [ ] **Step 5: Fix stale comments**

`AeroApiOptions.StatusWindowFutureDays` — replace the `<para>` that names `GetActiveTrackedFlightsAsync` with:

```csharp
        /// <para>
        /// <c>FlightSyncPlanner</c> reads the same value, through <c>IFlightStatusWindowOptions</c>, as
        /// the moment a tracked flight enters Live sync: this many days before its scheduled
        /// departure, the first moment this endpoint can answer for it (#343, #356).
        /// </para>
```

`AeroApiFlightStatusProvider.BuildUrl` — replace "`FlightRepository.GetActiveTrackedFlightsAsync`'s own window, which is derived from the same `AeroApiOptions`" with "`FlightSyncPlanner`'s Live window, which is derived from the same `AeroApiOptions`".

Check: `grep -rn -e GetActiveTrackedFlightsAsync -e IsActiveForFastPoll --include='*.cs' .` prints nothing.

- [ ] **Step 6: CLAUDE.md**

After the `FlightStatusSyncBackgroundService` publishes a heartbeat paragraph, add:

```markdown
**Each tracked flight is synced on its own schedule (#356).** `FlightSyncPlanner` (Application) gives a flight a mode from its times, status and tracking, first match wins: none (no traveler tracks it); `Stopped` (`FlightSync:StopAfterArrival` after its actual arrival or `CancelledAtUtc`, or `StopWithoutArrivalAfter` after an arrival that is never reported); `Fast` (from `FastWindowBeforeDeparture` before the *estimated*, else scheduled, departure; every `NearDepartureInterval`); `Live` (from `AeroApi:StatusWindowFutureDays` before the *scheduled* departure, the first moment `/flights/{ident}` can answer, #343; every `Interval`); `Schedule` (further out). The next sync is capped at the moment the mode would change, so a stored mode is at most one loop stale. `FlightStatusSyncBackgroundService` wakes every `LoopInterval` and sends `SyncFlightStatusCommand(null, id, SyncTrigger.Scheduled)` only for tracked flights with `NextSyncDueUtc <= now`, oldest due first, **each in its own DI scope** so a flight that fails part-way can't be saved by the next one's `SaveChanges`. The loop never calls AeroAPI for a Stopped flight; an admin or traveler sync still does, because a person asked. Schedule-mode flights make no call yet (the `/schedules` check is step 2 of #349) and are next due when Live starts. `LastSyncedUtc` records every call that returned, empty or duplicate included; a call that throws changes nothing, so the flight stays due and the next loop retries it. Tracking an unplanned flight makes it due now; the last untrack clears its mode but keeps `LastSyncedUtc`. Deleting a traveler doesn't clear plans; the loop ignores untracked flights, so that costs no calls.
```

In the Required Configuration table, after `AeroApi:MonthlyBudgetUsd`, add:

```markdown
| `FlightSync:Enabled` | Optional; default `true`. Set `false` for a local run, or it polls AeroAPI alongside the deployed app (#347) |
| `FlightSync:LoopInterval`, `NearDepartureInterval`, `Interval`, `ScheduleCheckInterval`, `FastWindowBeforeDeparture`, `StopAfterArrival`, `StopWithoutArrivalAfter` | Optional; defaults 1 min, 5 min (Fast), 30 min (Live), 1 day, 4 h, 30 min, 1 day; Fast and Live lengthened for cost in #364. Validated on start |
```

- [ ] **Step 7: Commit**

```bash
git add FlightAssistant.API.Tests/Integration/AdminFlightEndpointTests.cs FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs \
  FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiOptions.cs FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightStatusProvider.cs \
  CLAUDE.md
git commit -m "test: sync endpoints keep { updated }; docs for the per-flight sync schedule (#356)"
```

---
### Task 9: Rebase, final migration with backfill, OpenAPI, PR

**Files:**
- Create: migration `AddFlightSyncSchedule` (regenerated), with a backfill in `Up`
- Modify: `FlightAssistant.Infrastructure/Persistance/Migrations/FlightAssistantDbContextModelSnapshot.cs` (generated only)
- Test: `FlightAssistant.Infrastructure.Tests/Persistance/Migrations/AddFlightSyncScheduleMigrationTests.cs`
- Maybe: `spec/openapi.json` (regenerated only)

**Interfaces:**
- Consumes: Task 3's EF configuration and all committed work.
- Produces: the PR.

- [ ] **Step 1: Check for overlap before rebasing**

```bash
git status --short          # expect only the working migration (??) and the snapshot ( M)
git fetch origin
git log --oneline HEAD..origin/development
gh pr list --state open --json number,title,headRefName,files --jq '.[] | {number, title, files: [.files[].path]}'
```

If an open PR from the other agent changes a file this branch changed, other than `BrowsableTables.cs`, `appsettings.json`, `spec/openapi.json` or the model snapshot, **stop and ask the user**.

- [ ] **Step 2: Drop the working migration and rebase**

```bash
rm FlightAssistant.Infrastructure/Persistance/Migrations/*_AddFlightSyncSchedule.cs \
   FlightAssistant.Infrastructure/Persistance/Migrations/*_AddFlightSyncSchedule.Designer.cs
git checkout HEAD -- FlightAssistant.Infrastructure/Persistance/Migrations/FlightAssistantDbContextModelSnapshot.cs
git status --short          # expect clean
git rebase origin/development
```

Conflicts: in `BrowsableTables.cs`, `appsettings.json` or `CLAUDE.md`, keep both sides. In `spec/openapi.json` or the model snapshot, take `origin/development`'s version (`git checkout --theirs` is the *branch* side during a rebase, so use `git checkout origin/development -- <file>`); both are regenerated below. Anything else: stop and ask. Then `dotnet build` (0 errors).

- [ ] **Step 3: Regenerate the migration**

```bash
git diff --quiet origin/development -- FlightAssistant.Infrastructure/Persistance/Migrations/FlightAssistantDbContextModelSnapshot.cs \
  || git checkout origin/development -- FlightAssistant.Infrastructure/Persistance/Migrations/FlightAssistantDbContextModelSnapshot.cs
CONNECTION_STRING="Host=localhost;Port=5432;Database=flightassistant;Username=fa;Password=fa" \
  dotnet ef migrations add AddFlightSyncSchedule --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
```

`Up` must contain only: five nullable `AddColumn`s on `flights` (`CancelledAtUtc`, `LastSyncedUtc`, `NextSyncDueUtc`, `ScheduleMissingSinceUtc` as `timestamp with time zone`; `SyncMode` as `character varying(16)`) and `CreateIndex ix_flight_next_sync_due`. If it contains another branch's changes, the snapshot wasn't reset: delete the files and repeat this step.

- [ ] **Step 4: Write the failing backfill test**

```csharp
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Migrations;

/// <summary>
/// The <c>AddFlightSyncSchedule</c> backfill (#356): every tracked flight becomes due at once, so the
/// first loop plans it; untracked flights stay unplanned. Runs in a database of its own on the shared
/// container, migrated only to the migration before this one, so rows exist when the backfill runs.
/// The shared, fully migrated database isn't touched.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AddFlightSyncScheduleMigrationTests : IAsyncLifetime
{
    private const string MigrationSuffix = "_AddFlightSyncSchedule";

    private readonly PostgresContainerFixture _fixture;
    private readonly string _databaseName = "backfill_" + Guid.NewGuid().ToString("N");
    private readonly string _connectionString;

    public AddFlightSyncScheduleMigrationTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
        _connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = _databaseName }.ConnectionString;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var db = _fixture.CreateDbContext();
        var drop = "DROP DATABASE IF EXISTS \"" + _databaseName + "\" WITH (FORCE)";
        await db.Database.ExecuteSqlRawAsync(drop);
    }

    // Public: EF materializes it for SqlQueryRaw.
    public sealed class FlightRow
    {
        public string FlightNumber { get; set; } = "";
        public DateTime? NextSyncDueUtc { get; set; }
        public string? SyncMode { get; set; }
    }

    [Fact]
    public async Task Up_MakesTrackedFlightsDueNow_AndLeavesUntrackedFlightsUnplanned()
    {
        await using var db = new FlightAssistantDbContext(
            new DbContextOptionsBuilder<FlightAssistantDbContext>().UseNpgsql(_connectionString).Options);

        // Found by name, not hardcoded, so a migration merged in before this one doesn't break the test.
        var migrations = db.Database.GetMigrations().ToList();
        var index = migrations.FindIndex(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        Assert.True(index > 0, "AddFlightSyncSchedule migration not found.");
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(migrations[index - 1]);

        // TK1 tracked once, TK2 twice, TK3 not at all. Foreign keys are off for the seed: the backfill
        // reads only flights and traveler_flights, and the parent tables' columns at this schema
        // version aren't this test's concern.
        const string seed = """
            SET session_replication_role = replica;
            INSERT INTO flights ("AirlineId", "FlightNumber", "DepartureAirportId", "ArrivalAirportId", "ServiceDateUtc", "ScheduledDepartureUtc", "ScheduledArrivalUtc", "CreatedUtc", "UpdatedUtc")
            VALUES (1, 'TK1', 1, 2, DATE '2026-09-20', TIMESTAMPTZ '2026-09-20 08:00Z', TIMESTAMPTZ '2026-09-20 11:00Z', now(), now()),
                   (1, 'TK2', 1, 2, DATE '2026-09-20', TIMESTAMPTZ '2026-09-20 08:00Z', TIMESTAMPTZ '2026-09-20 11:00Z', now(), now()),
                   (1, 'TK3', 1, 2, DATE '2026-09-20', TIMESTAMPTZ '2026-09-20 08:00Z', TIMESTAMPTZ '2026-09-20 11:00Z', now(), now());
            INSERT INTO traveler_flights ("Id", "TravelerId", "FlightId", "Notify", "CreatedUtc")
            SELECT gen_random_uuid(), gen_random_uuid(), "Id", true, now() FROM flights WHERE "FlightNumber" IN ('TK1', 'TK2')
            UNION ALL
            SELECT gen_random_uuid(), gen_random_uuid(), "Id", true, now() FROM flights WHERE "FlightNumber" = 'TK2';
            SET session_replication_role = origin;
            """;
        await db.Database.ExecuteSqlRawAsync(seed);

        var before = DateTime.UtcNow;
        await migrator.MigrateAsync(migrations[index]);
        var after = DateTime.UtcNow;

        var rows = (await db.Database
                .SqlQueryRaw<FlightRow>("SELECT \"FlightNumber\", \"NextSyncDueUtc\", \"SyncMode\" FROM flights")
                .ToListAsync())
            .ToDictionary(r => r.FlightNumber);

        // A minute's slack either side for clock drift between the container and the host.
        Assert.InRange(rows["TK1"].NextSyncDueUtc ?? DateTime.MinValue, before.AddMinutes(-1), after.AddMinutes(1));
        Assert.InRange(rows["TK2"].NextSyncDueUtc ?? DateTime.MinValue, before.AddMinutes(-1), after.AddMinutes(1));
        Assert.Null(rows["TK3"].NextSyncDueUtc);
        Assert.All(rows.Values, r => Assert.Null(r.SyncMode)); // the first loop plans them
    }
}
```

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AddFlightSyncScheduleMigrationTests"`
Expected: FAIL — TK1's `NextSyncDueUtc` is null.

- [ ] **Step 5: Add the backfill** — in the migration's `Up`, after the `CreateIndex`:

```csharp
            // Every tracked flight is due at once, so the first loop plans it (spec §2.1). Others stay null.
            migrationBuilder.Sql("""
                UPDATE flights f
                SET "NextSyncDueUtc" = now()
                WHERE EXISTS (SELECT 1 FROM traveler_flights tf WHERE tf."FlightId" = f."Id");
                """);
```

Run the test: PASS.

- [ ] **Step 6: Prove**
  1. Delete the `WHERE EXISTS …` line: the test fails on TK3.
  2. Change `tf."FlightId" = f."Id"` to `tf."FlightId" <> f."Id"`: it fails on TK1.
  Restore each (`mv … .bak`, `touch`).

- [ ] **Step 7: Regenerate the OpenAPI spec**

```bash
scripts/generate-openapi.sh
git diff --stat spec/openapi.json
```

No diff is expected (routes and response shapes are unchanged). If there is one, read it: keep it only if it comes from this branch's code; otherwise stop and ask.

- [ ] **Step 8: Full verification**

```bash
dotnet build --configuration Release 2>&1 | tail -3     # 0 errors
dotnet test 2>&1 | grep -E "^(Passed|Failed)!|error"      # all four projects pass
```

Record the per-project pass counts for the PR.

- [ ] **Step 9: Commit**

```bash
git status --short   # only the new migration pair, the snapshot, the new test, maybe spec/openapi.json
git add FlightAssistant.Infrastructure/Persistance/Migrations/*_AddFlightSyncSchedule.cs \
  FlightAssistant.Infrastructure/Persistance/Migrations/*_AddFlightSyncSchedule.Designer.cs \
  FlightAssistant.Infrastructure/Persistance/Migrations/FlightAssistantDbContextModelSnapshot.cs \
  FlightAssistant.Infrastructure.Tests/Persistance/Migrations/AddFlightSyncScheduleMigrationTests.cs
git diff --quiet spec/openapi.json || git add spec/openapi.json
git commit -m "feat: AddFlightSyncSchedule migration, tracked flights due at once (#356)"
```

- [ ] **Step 10: Check development again**

`git fetch origin && git log --oneline HEAD..origin/development`. If it gained commits, repeat Steps 1–9: delete this migration pair, reset the snapshot to `origin/development`'s, rebase, regenerate, re-add the backfill, re-run. Never resolve a snapshot conflict by hand.

- [ ] **Step 11: Push and open the PR**

```bash
git push -u origin feature/356-flight-sync-planner
gh pr create --base development --title "feat: per-flight sync schedule, step 1 — planner, due-only loop (#356)" --body-file /private/tmp/claude-501/-Users-salihalishov-Repositories-flight-assistant-backend/4f981baf-ef1b-4b22-b065-df40c3650ae3/scratchpad/pr-356.md
```

PR body sections:
- **Summary**: what changed, `Closes #356, closes #343, closes #344, closes #345, closes #350` (#350's validator half merged in #363; its sync half is the 24 h no-arrival Stopped rule here). Say this is Phase 1 item 1 of #364.
- **Decisions**: PascalCase columns (repo convention, spec wrote snake_case); `SyncMode` stored as text; Schedule mode makes no call and is next due at Live start; one DI scope per flight; `ApplyStatusUpdate` now takes `nowUtc`; `FlightRepository` no longer takes `AeroApiOptions`; Fast and Live defaults lengthened to 5 and 30 min for cost (#364, Task 11), spec updated to match; branch rebased onto `development` before Task 6 (Task 10).
- **Tests that were seen failing**: a table of each deliberate break and the test that caught it, from every task's Prove step.
- **Not in this PR / known gaps**: `/schedules` check and `ScheduleMissingSinceUtc` writes (step 2, which #364 reshapes: weekly until 7 days before departure, then daily, with the known `DepartureIata` in the criteria); admin DTOs (step 3); traveler deletion leaves a stale `SyncMode` (no calls); #353's heartbeat duration; the Task 8 Step 4 finding on how `SyncMode` shows in the data grid.
- **Deploy note**: the migration backfill makes every tracked flight due at once; with the 12 s throttle, N tracked flights take about 12 s × N (Fast/Live only; Schedule and Stopped make no call) on the first loop.
- End with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

- [ ] **Step 12: Stop.** Don't merge. Report to the user in short bullets: done, left, needed from them.

---
### Task 10: Rebase onto `development` before Task 6 (amendment, #364)

**Why now:** `development` gained 16 commits since this branch started, and several touch files Tasks 7–8 edit: `FlightRepository.cs` and `PagedRepositoryArgumentOrderTests.cs` (#328, deterministic paged order), `FlightEndpointTests.cs` and `ValidatorEdgeCaseTests.cs` (#350), `AeroApiDependencyInjectionTests.cs` (#308, #350), `BrowsableTables.cs` (#355), `CLAUDE.md`, plus a newer migration `20260914204216_AddVerificationSourceAndIssue`. Rebasing only in Task 9 would mean resolving those conflicts against code written on stale files.

**Files:** none new. Rewrites the local, unpushed branch history. The working migration is backed up, dropped, and regenerated (still uncommitted).

**Interfaces:** none change. Produces: the same commits, rebased; a working migration that sits after `AddVerificationSourceAndIssue`.

- [ ] **Step 1: Check the state**

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-356
git status --short     # expect only: ?? the AddFlightSyncSchedule migration pair,  M the model snapshot
git fetch origin
git log --oneline HEAD..origin/development
gh pr list --state open --json number,title,headRefName
git rev-parse HEAD     # OLD_HEAD — write it in the report
```

If an open PR changes a file this branch changes, stop and report BLOCKED.

- [ ] **Step 2: Back up and drop the working migration**

```bash
B=.superpowers/sdd/2026-09-14-flight-sync-planner/migration-backup-pre-rebase
mkdir -p "$B"
cp FlightAssistant.Infrastructure/Persistance/Migrations/*_AddFlightSyncSchedule*.cs "$B"/
git diff FlightAssistant.Infrastructure/Persistance/Migrations/FlightAssistantDbContextModelSnapshot.cs > "$B"/snapshot.diff
rm FlightAssistant.Infrastructure/Persistance/Migrations/*_AddFlightSyncSchedule.cs \
   FlightAssistant.Infrastructure/Persistance/Migrations/*_AddFlightSyncSchedule.Designer.cs
git checkout HEAD -- FlightAssistant.Infrastructure/Persistance/Migrations/FlightAssistantDbContextModelSnapshot.cs
git status --short     # expect clean (.superpowers is ignored)
```

- [ ] **Step 3: Rebase**

```bash
git rebase origin/development
```

Conflict rules:
- `BrowsableTables.cs`, `appsettings.json`, `CLAUDE.md`: keep both sides.
- `FlightRepository.cs`: keep `development`'s ordering changes (#328) **and** the branch's `GetDueTrackedFlightIdsAsync` / `IsFlightTrackedAsync`.
- `ValidatorEdgeCaseTests.cs`, `AeroApiDependencyInjectionTests.cs`, `FlightEndpointTests.cs`: keep every test from both sides. Where the branch changed a line that `development` also changed, apply the branch's intent (for example a new constructor argument) to `development`'s version.
- Model snapshot or `spec/openapi.json`: take `development`'s version (`git checkout origin/development -- <file>`); both are regenerated later.
- Anything else, or a conflict whose right resolution you can't state in one sentence: `git rebase --abort` and report BLOCKED, naming the file and both sides.

Before each `git rebase --continue`, `dotnet build` must report 0 errors, so every rewritten commit still builds. Record each conflict and its resolution in the report.

- [ ] **Step 4: Regenerate the working migration (uncommitted)**

```bash
git diff --quiet origin/development -- FlightAssistant.Infrastructure/Persistance/Migrations/FlightAssistantDbContextModelSnapshot.cs \
  && echo "snapshot matches development" || echo "SNAPSHOT DIFFERS — stop and report"
CONNECTION_STRING="Host=localhost;Port=5432;Database=flightassistant;Username=fa;Password=fa" \
  dotnet ef migrations add AddFlightSyncSchedule --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
```

`Up` must contain only the five nullable `AddColumn`s on `flights` and `CreateIndex ix_flight_next_sync_due`: compare it with the backup. Do not commit it. Task 9 regenerates it again and adds the backfill.

- [ ] **Step 5: Verify**

```bash
dotnet build 2>&1 | tail -3                              # 0 errors
dotnet test 2>&1 | grep -E "^(Passed|Failed)!|error"     # all four projects; Infrastructure needs Docker
git status --short                                        # only the working migration pair (??) and the snapshot ( M)
git log --oneline origin/development..HEAD                # same commit subjects as before, in the same order
git range-diff $(git merge-base OLD_HEAD origin/development)..OLD_HEAD origin/development..HEAD \
  > .superpowers/sdd/2026-09-14-flight-sync-planner/rebase-range-diff.txt
```

(Substitute the literal OLD_HEAD SHA from Step 1.) If any test fails, report it with its output; don't change code to fix it in this task.

- [ ] **Step 6: No commit, no push.** Report: OLD_HEAD, the old → new SHA of each commit, every conflict and its resolution, build result, per-project test counts, and the range-diff path.

---
### Task 11: Cheaper Fast and Live defaults (amendment, #364)

**Why:** the cost plan (`docs/superpowers/plans/2026-09-15-flight-data-cost-reduction.md`, Phase 1 item 1) lengthens the Fast interval from 2 to 5 minutes and the Live interval from 10 to 30 minutes. That takes a typical tracked flight from ~740 status calls to ~180 (≈ $0.90 at $0.005 a call). The key events (departed, landed, cancelled, diverted, gate changes, delays over 30 min) still land within one interval.

**Files:**
- Modify: `FlightAssistant.API/HostedServices/FlightSyncOptions.cs` (two defaults)
- Modify: `FlightAssistant.API/appsettings.json` (the same two values)
- Test: `FlightAssistant.API.Tests/HostedServices/FlightSyncOptionsTests.cs`
- Modify: `docs/superpowers/specs/2026-09-14-flight-sync-schedule-design.md` (§1, §1.1, §2.7 and §4 tables)

**Interfaces:** none change. Only the `FlightSyncOptions` defaults and the shipped `appsettings.json` values.

**Values:** `NearDepartureInterval` (Fast) `00:05:00`; `Interval` (Live) `00:30:00`. Everything else unchanged. Both are inside the existing `[Range]`s.

- [ ] **Step 1: Write the failing tests**

In `Defaults_MatchTheSpec`, change the two asserts to `TimeSpan.FromMinutes(5)` (`NearDepartureInterval`) and `TimeSpan.FromMinutes(30)` (`Interval`).

Add a test that the shipped `appsettings.json` can't drift from the class defaults (the deployed app binds the file, which overrides the class):

```csharp
    [Fact]
    public void AppSettingsJson_FlightSyncSection_MatchesTheCodeDefaults()
    {
        // The deployed app binds appsettings.json, which overrides the class defaults. If the two
        // disagree, the defaults that tests and docs describe aren't the ones that run.
        var config = new ConfigurationBuilder().AddJsonFile(ApiAppSettingsPath(), optional: false).Build();
        var shipped = new FlightSyncOptions();
        config.GetSection(FlightSyncOptions.SectionName).Bind(shipped);
        var defaults = new FlightSyncOptions();

        Assert.Equal(defaults.Enabled, shipped.Enabled);
        Assert.Equal(defaults.LoopInterval, shipped.LoopInterval);
        Assert.Equal(defaults.NearDepartureInterval, shipped.NearDepartureInterval);
        Assert.Equal(defaults.Interval, shipped.Interval);
        Assert.Equal(defaults.ScheduleCheckInterval, shipped.ScheduleCheckInterval);
        Assert.Equal(defaults.FastWindowBeforeDeparture, shipped.FastWindowBeforeDeparture);
        Assert.Equal(defaults.StopAfterArrival, shipped.StopAfterArrival);
        Assert.Equal(defaults.StopWithoutArrivalAfter, shipped.StopWithoutArrivalAfter);
    }

    /// <summary>The API project's own appsettings.json, found by walking up from the test output directory.</summary>
    private static string ApiAppSettingsPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "FlightAssistant.API", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("FlightAssistant.API/appsettings.json not found above " + AppContext.BaseDirectory);
    }
```

Before adding the helper, grep `FlightAssistant.API.Tests` for an existing way of locating the repository root or the API project directory; reuse it if there is one.

- [ ] **Step 2: Run, expect `Defaults_MatchTheSpec` to FAIL** (2 min ≠ 5 min). The new test passes at this point: file and class still agree at 2 / 10.

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightSyncOptionsTests"`

- [ ] **Step 3: Change the class defaults** to `TimeSpan.FromMinutes(5)` and `TimeSpan.FromMinutes(30)`. Run again: `Defaults_MatchTheSpec` PASSES and `AppSettingsJson_FlightSyncSection_MatchesTheCodeDefaults` FAILS, because the file still says `00:02:00` / `00:10:00`. Record that failure; it is the new test's proof.

- [ ] **Step 4: Change `appsettings.json`**: `"Interval": "00:30:00"`, `"NearDepartureInterval": "00:05:00"`. Run: all `FlightSyncOptionsTests` PASS. Then check that no other `appsettings*.json` sets these keys (`grep -rn --include='appsettings*.json' -e NearDepartureInterval -e '"Interval"' . | grep -v -e /bin/ -e /obj/`); if one does, report it rather than changing it.

- [ ] **Step 5: Prove** — set only `appsettings.json`'s `"Interval"` back to `"00:10:00"`: the new test fails on `Interval`. Restore.

- [ ] **Step 6: Update the spec** (`docs/superpowers/specs/2026-09-14-flight-sync-schedule-design.md`), numbers only:
  - §1 table, "Synced every": Fast `2 min` → `5 min`; Live `10 min` → `30 min`.
  - §1.1 table: Fast `now + 2 min` → `now + 5 min`; Live `min(now + 10 min, start of Fast)` → `min(now + 30 min, start of Fast)`.
  - §2.7 table: `NearDepartureInterval` row → default `00:05:00`, status `existing, now the Fast interval; lengthened from 00:02:00 for cost (#364)`. `Interval` row → default `00:30:00`, status `existing, now the Live interval; lengthened from 00:10:00 for cost (#364)`.
  - §4 table: Fast `720` → `288`; Live `144` → `48`.
  - Leave the Overview alone: its "10 minutes, or 2 minutes" describes the code before this spec.

- [ ] **Step 7: Run the whole `FlightAssistant.API.Tests` project**, then commit:

```bash
git add FlightAssistant.API/HostedServices/FlightSyncOptions.cs FlightAssistant.API/appsettings.json \
  FlightAssistant.API.Tests/HostedServices/FlightSyncOptionsTests.cs \
  docs/superpowers/specs/2026-09-14-flight-sync-schedule-design.md
git commit -m "feat: cheaper sync defaults, Fast every 5 min and Live every 30 min (#364)"
```
