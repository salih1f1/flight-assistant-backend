# Delay Pushes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delays are detected from times, and pushes go out only when the status changes or a delay grows by 15 minutes (#352).
- **Detection.** A delayed flight is reported as `DLY` whether AeroAPI writes "Delayed" or "Scheduled / Delayed".
- **When a push goes out.** Only when the status actually changes, or when a delay grows by at least 15 minutes since the last delay push.
- **Wording.** A delay push says how many minutes late the flight is.

**Architecture:**
- **Provider (Infrastructure).**
  - `AeroApiFlightStatusProvider` decides a delay from `scheduled_out` and `estimated_out` before departure, whatever the prose says.
  - "Taxiing" maps to `DEP`.
  - The 15-minute threshold is one constant: `Flight.DelayThreshold`.
- **Domain.**
  - `Flight.ApplyStatusUpdate` takes whether the status changed, and decides whether travelers should be pushed.
  - For a delay, it compares the new estimate with `Flight.DelayNotifiedDepartureUtc`, the estimate the last delay push went out for (a new column, stored in the same save as the event).
  - `FlightStatusChanged` carries `ShouldNotify` and `DelayMinutes`.
- **Application.**
  - `SyncFlightStatusCommandHandler` compares the new status with the flight's latest event to get "status changed".
  - `FlightStatusChangedNotificationHandler` skips an event that shouldn't notify, and words a delay as minutes late.

**Tech Stack:** .NET 10, EF Core + Npgsql (one migration), MediatR, xUnit, NSubstitute, Testcontainers PostgreSQL (#295).

**Spec:** GitHub issue #352, plus the user's decisions of 2026-09-16:
- **Delay push wording:** "Minutes late": "Your flight is delayed by 40 min."
- **Re-push:** "15+ min since last push". Store the last notified estimate on the flight (one small migration) so small slips add up.
- **Real status strings:** "Handle both forms". Detect delays from times, whichever form AeroAPI uses; map "Taxiing" to `DEP`; no dev database access.

## Global Constraints

- Work only in the worktree `/Users/salihalishov/Repositories/flight-assistant-backend-352`, branch `feature/352-delay-pushes`. Never touch the main checkout or other worktrees.
- **The threshold.** `public static readonly TimeSpan DelayThreshold = TimeSpan.FromMinutes(15);` on `FlightAssistant.Domain.Flights.Entities.Flight`, used for both detection and re-push. It isn't a setting.
- **Delay detection** in `AeroApiFlightStatusProvider.MapStatusCode`:
  1. `cancelled` → `CAN`, then `diverted` → `DIV`, as today.
  2. **Picking a code from the text.** The segment before the first `/` (trimmed, case-insensitive) picks a code:
     - `scheduled` → `SCH`
     - `en route`, `departed` or `taxiing` → `DEP`
     - `arrived` or `landed` → `ARR`
     - `delayed` → `DLY`
     - `diverted` → `DIV`
     - `cancelled` → `CAN`
     - anything else → `UNK`, with a warning, as today.
  3. **Applying times.** If the code is `SCH` or `DLY`:
     - with both `scheduled_out` and `estimated_out` present, it becomes `DLY` when `estimated_out − scheduled_out >= Flight.DelayThreshold`, and `SCH` otherwise;
     - without both times, it becomes `DLY` if any later `/` segment is `delayed`, and otherwise stays as picked.
- **The push decision** in `Flight.ApplyStatusUpdate(..., DateTime nowUtc, bool statusChanged = true)`:
  - `ShouldNotify` starts as `statusChanged`.
  - **For a delay with an estimate.** When the status is `DLY` and the estimated departure is present:
    - `DelayMinutes` = the estimate minus `ScheduledDepartureUtc`, rounded to whole minutes, when the estimate is after the schedule; otherwise null.
    - `ShouldNotify` also becomes true when `DelayNotifiedDepartureUtc` is null, or when the estimate is at least `DelayThreshold` later than it.
    - When `ShouldNotify` ends up true, `DelayNotifiedDepartureUtc` is set to the estimate.
  - **For a delay without an estimate,** `DelayMinutes` is null and only `statusChanged` counts.
  - `DelayNotifiedDepartureUtc` is never cleared. A return to `DLY` from another status is a status change and pushes anyway.
- **The event.** `FlightStatusChanged(int FlightId, string FlightNumber, DateOnly ServiceDateUtc, string StatusCode, DateTime OccurredUtc, bool ShouldNotify = true, int? DelayMinutes = null)`.
- **Status changed.** `SyncFlightStatusCommandHandler`'s new-status path reads `GetLatestFlightStatusEventAsync` before changing the flight, and passes `statusChanged: latest?.StatusId != statusId.Value`. The known-status path (#351) is unchanged and never pushes.
- **The push.**
  - `FlightStatusChangedNotificationHandler` returns before any lookup when `ShouldNotify` is false.
  - The `DLY` body is `$"Your flight is delayed by {DelayMinutes} min."` when `DelayMinutes` has a value, and `"Your flight is delayed."` otherwise.
  - Every other message is unchanged.
- **Persistence.**
  - `Flight.DelayNotifiedDepartureUtc` (`DateTime?`) is a nullable `timestamp with time zone` column on `flights`, added by the EF migration `AddFlightDelayNotifiedDeparture`. The migration is generated with `dotnet ef`, not hand-written, and adds nothing else.
  - The column is listed as exposed in `BrowsableTables` for `flights`.
- **API.** No DTO, route or `spec/openapi.json` change.
- **No paid calls, no dev database.** Make no call to AeroAPI or any other paid external API, and don't access the dev database (the user's decision). Never run the API with sync on. Never run `dotnet ef database update`.
- **SQL tests.** Behaviour that depends on SQL (the new column's round trip) is proved against PostgreSQL through the Testcontainers harness (#295).
- **The build stays warning-free** (#301).
- **TDD.** Every new test is seen failing first. Each task ends with a prove step: break the code on purpose, watch the named test fail, then restore. After restoring from a `.bak`, `touch` the file so MSBuild recompiles.
- **Commits.** Commit with explicit paths only (never `-A` or `.`). Every commit message ends with a blank line and the attribution trailer your own instructions require. Never push.

## Decisions made while planning

These are recorded so review doesn't relitigate them.

- **Times decide both ways.** A "Delayed" text with an on-time estimate is `SCH`, and a "Scheduled" text with an estimate 20 minutes late is `DLY`. That's the user's "detect delays from times", and stale prose can't keep pushing.
  - The cost: if AeroAPI ever flags a delay from something other than `estimated_out`, it shows as `SCH`.
- **The push decision lives in the Domain,** so the remembered estimate saves in the same `SaveChanges` as the event and can't drift from what was actually pushed.
- **"Status changed" compares status ids, in the handler.** `FlightStatus` has no public constructor, so a test can't give a stubbed event a `Status` navigation. Comparing `latest.StatusId` with the resolved id avoids that.
- **`statusChanged` is optional and defaults to `true`,** so the Domain and planner tests that call `ApplyStatusUpdate` keep compiling. The one production caller passes it explicitly, and a handler test pins that.
- **Minutes late, not a clock time,** so the message needs no timezone or locale (the user's decision).
- **A flight already delayed when this ships** has no remembered estimate, so its next delayed sync pushes once.
- **Rounding.** A delay of 39.5 minutes reads "40 min".

## Commands

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-352
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiFlightStatusProviderTests"
dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj --filter "FullyQualifiedName~FlightTests"
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FlightRepositoryTests|FullyQualifiedName~BrowsableTables"
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests|FullyQualifiedName~FlightStatusChangedNotificationHandlerTests"
CONNECTION_STRING="Host=localhost;Database=design-time" dotnet ef migrations add AddFlightDelayNotifiedDeparture --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
dotnet build --configuration Release
```

---

### Task 1: A delay comes from the times

**Files:**
- Modify: `FlightAssistant.Domain/Flights/Entities/Flight.cs` (add `DelayThreshold` only)
- Modify: `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightStatusProvider.cs`
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiFlightStatusProviderTests.cs`

**Interfaces:**
- Produces: `public static readonly TimeSpan Flight.DelayThreshold` (15 minutes). Tasks 2 and 3 consume it.

- [ ] **Step 1: Write the failing provider tests.** In `AeroApiFlightStatusProviderTests.cs`:

(a) Give `SingleFlightBody` an estimated-departure parameter. Existing callers keep the on-time default. Replace its signature and its `estimated_out` line:

```csharp
    private static string SingleFlightBody(string? status, bool cancelled = false, bool diverted = false,
        string? estimatedOut = "2026-08-30T06:35:00Z")
```

```csharp
              "estimated_out": {{(estimatedOut is null ? "null" : $"\"{estimatedOut}\"")}},
```

(b) In `GetStatusAsync_MapsKnownStatusProse_ToTheDocumentedCode`:
- **delete** the row `[InlineData("Delayed", "DLY")]`. With the helper's on-time estimate, the times now say `SCH`; the new theory below covers delays.
- **add** the row `[InlineData("Taxiing / Left Gate", "DEP")]`.

(c) Directly after that theory, add:

```csharp
    [Theory]
    [InlineData("Scheduled / Delayed", "2026-08-30T07:15:00Z", "DLY")] // 40 min late
    [InlineData("Scheduled", "2026-08-30T06:55:00Z", "DLY")]           // 20 min late: the times say delayed
    [InlineData("Delayed", "2026-08-30T06:50:00Z", "DLY")]             // exactly the 15 min threshold
    [InlineData("Scheduled", "2026-08-30T06:49:00Z", "SCH")]           // 14 min: under the threshold
    [InlineData("Scheduled / Delayed", "2026-08-30T06:45:00Z", "SCH")] // 10 min: the times win over the text
    [InlineData("Delayed", "2026-08-30T06:35:00Z", "SCH")]             // on time
    [InlineData("En Route / Delayed", "2026-08-30T07:15:00Z", "DEP")]  // departed: the times don't apply
    [InlineData("Taxiing / Left Gate", "2026-08-30T07:15:00Z", "DEP")]
    public async Task GetStatusAsync_BeforeDeparture_ADelayComesFromTheTimes(
        string statusText, string estimatedOut, string expectedCode)
    {
        // Scheduled out 06:35 (#352).
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: statusText, estimatedOut: estimatedOut));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal(expectedCode, result!.StatusCode);
    }

    [Theory]
    [InlineData("Scheduled / Delayed", "DLY")]
    [InlineData("Delayed", "DLY")]
    [InlineData("Scheduled", "SCH")]
    public async Task GetStatusAsync_WithoutAnEstimatedDeparture_TheTextDecides(string statusText, string expectedCode)
    {
        var handler = new StubHttpMessageHandler(SingleFlightBody(status: statusText, estimatedOut: null));

        var result = await Provider(handler).GetStatusAsync("TK1028", new DateOnly(2026, 8, 30), StatusCallKind.Interactive);

        Assert.Equal(expectedCode, result!.StatusCode);
    }
```

- [ ] **Step 2: Run the tests and watch them fail.**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiFlightStatusProviderTests"`

Expected, against today's text-only mapping:
- **Fail:**
  - in the prose theory: the new `Taxiing / Left Gate` row (actual `UNK`);
  - in `…_ADelayComesFromTheTimes`: `Scheduled / Delayed` at 40 min (actual `SCH`), `Scheduled` at 20 min (`SCH`), `Delayed` on time (`DLY`), and `Taxiing / Left Gate` (`UNK`);
  - in `…_TheTextDecides`: `Scheduled / Delayed` (actual `SCH`).
- **Pass already, because today's mapping happens to agree** (record them as "passes before"):
  - `Delayed` at exactly 15 min;
  - `Scheduled` at 14 min;
  - `Scheduled / Delayed` at 10 min;
  - `En Route / Delayed`;
  - `Delayed` and `Scheduled` without an estimate.

- [ ] **Step 3: Add the threshold.** In `Flight.cs`, directly below `public const string CancelledStatusCode = "CAN";`, add:

```csharp

        /// <summary>
        /// How late an estimated departure must be before a flight counts as delayed, and how much later a
        /// delay must grow before a traveler hears about it again (#352).
        /// </summary>
        public static readonly TimeSpan DelayThreshold = TimeSpan.FromMinutes(15);
```

- [ ] **Step 4: Detect delays from the times.** In `AeroApiFlightStatusProvider.cs`:

(a) Add `using FlightAssistant.Domain.Flights.Entities;` if it's missing.

(b) In `MapToResult`, replace

```csharp
            var statusRaw = TryGetString(flight, "status");
            var statusCode = MapStatusCode(statusRaw, cancelled, diverted, criteria);

            var estimatedOut = TryGetUtcDateTime(flight, "estimated_out");
```

with

```csharp
            var statusRaw = TryGetString(flight, "status");

            var estimatedOut = TryGetUtcDateTime(flight, "estimated_out");
```

and replace these two lines. `var scheduledOut = …` also appears earlier in the file, in the service-date match, so match both lines together; this pair occurs only in `MapToResult`:

```csharp
            var actualArrival = TryGetUtcDateTime(flight, "actual_in") ?? TryGetUtcDateTime(flight, "actual_on");
            var scheduledOut = TryGetUtcDateTime(flight, "scheduled_out");
```

with

```csharp
            var actualArrival = TryGetUtcDateTime(flight, "actual_in") ?? TryGetUtcDateTime(flight, "actual_on");
            var scheduledOut = TryGetUtcDateTime(flight, "scheduled_out");
            var statusCode = MapStatusCode(statusRaw, cancelled, diverted, scheduledOut, estimatedOut, criteria);
```

(c) Replace the whole `MapStatusCode` method, including its summary, with:

```csharp
        /// <summary>
        /// Normalises AeroAPI's prose <c>status</c> to the <c>FlightStatus.Code</c> vocabulary
        /// (<c>SCH</c>/<c>DEP</c>/<c>ARR</c>/<c>DLY</c>/<c>CAN</c>/<c>DIV</c>/<c>UNK</c>).
        /// <paramref name="cancelled"/> and <paramref name="diverted"/> are checked first and
        /// unconditionally win, in that order — AeroAPI supplies both as booleans and can report a
        /// stale "Scheduled"/"En Route" string on a flight that has in fact been cancelled or
        /// diverted, so the prose must never be allowed to mask either. (A flight cannot be both;
        /// if the payload claims it is, cancellation is the more consequential of the two for a
        /// traveller and wins.) The segment before the first "/" picks the code, compared
        /// case-insensitively against a deliberately small, documented set ("Taxiing" counts as
        /// departed) — anything outside it is logged, not guessed at.
        ///
        /// <para>
        /// <b>Before departure, a delay comes from the times, not the prose (#352).</b> AeroAPI writes a
        /// delay both as a qualifier ("Scheduled / Delayed") and as a leading "Delayed", so the text
        /// alone isn't reliable. With both <paramref name="scheduledOut"/> and
        /// <paramref name="estimatedOut"/>, a scheduled or delayed flight is <c>DLY</c> when the estimate
        /// is at least <see cref="Flight.DelayThreshold"/> late and <c>SCH</c> otherwise, whatever the text
        /// says. Without both times, a "Delayed" segment anywhere in the text still means <c>DLY</c>.
        /// </para>
        /// </summary>
        private string MapStatusCode(
            string? status, bool cancelled, bool diverted, DateTime? scheduledOut, DateTime? estimatedOut, string criteria)
        {
            if (cancelled) return StatusCancelled;
            if (diverted) return StatusDiverted;

            var segments = status?.Split('/').Select(s => s.Trim().ToLowerInvariant()).ToArray() ?? [];

            var code = segments.FirstOrDefault() switch
            {
                "scheduled" => StatusScheduled,
                "en route" or "departed" or "taxiing" => StatusDeparted,
                "arrived" or "landed" => StatusArrived,
                "delayed" => StatusDelayed,
                "diverted" => StatusDiverted,
                "cancelled" => StatusCancelled,
                _ => null
            };

            if (code is null)
                return LogUnmappedStatus(status, criteria);

            if (code == StatusScheduled || code == StatusDelayed)
            {
                if (scheduledOut is { } scheduled && estimatedOut is { } estimated)
                    return estimated - scheduled >= Flight.DelayThreshold ? StatusDelayed : StatusScheduled;

                if (segments.Skip(1).Contains("delayed"))
                    return StatusDelayed;
            }

            return code;
        }
```

- [ ] **Step 5: Run the tests and watch them pass.**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiFlightStatusProviderTests"`
Expected: all PASS. That includes `GetStatusAsync_MapsScheduled_ToSch` and the fixture's "Scheduled" rows, whose estimate equals the schedule.

- [ ] **Step 6: Prove the tests.** Make each break, run the named test, see it fail, then restore with `mv <file>.bak <file> && touch <file>`.
1. Delete the `if (scheduledOut is { } scheduled && estimatedOut is { } estimated) return …;` line. `…_ADelayComesFromTheTimes` fails for `Scheduled` at 20 min and for `Delayed` on time.
2. Change `>=` to `>`. `…_ADelayComesFromTheTimes` fails for `Delayed` at exactly 15 min.
3. Remove `or "taxiing"`. The `Taxiing / Left Gate` rows fail.
4. Delete the `if (segments.Skip(1).Contains("delayed")) return StatusDelayed;` line. `…_TheTextDecides` fails for `Scheduled / Delayed`.

- [ ] **Step 7: Build.** Run `dotnet build --configuration Release`. Expected: 0 errors, 0 warnings.

- [ ] **Step 8: Commit.**

```bash
git add FlightAssistant.Domain/Flights/Entities/Flight.cs \
  FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightStatusProvider.cs \
  FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiFlightStatusProviderTests.cs
git commit -m "fix: detect a delay from the departure times, whichever form AeroAPI writes it in, and map taxiing to departed (#352)"
```

(End the message with a blank line and your attribution trailer.)

---

### Task 2: The flight decides whether a status update should push

**Files:**
- Modify: `FlightAssistant.Domain/Flights/Entities/Flight.cs`
- Modify: `FlightAssistant.Domain/Flights/Events/FlightStatusChanged.cs`
- Modify: `FlightAssistant.Application/Administration/BrowsableTables.cs`
- Create (generated): `FlightAssistant.Infrastructure/Persistance/Migrations/<timestamp>_AddFlightDelayNotifiedDeparture.cs` and `.Designer.cs`
- Modify (generated): `FlightAssistant.Infrastructure/Persistance/Migrations/FlightAssistantDbContextModelSnapshot.cs`
- Test: `FlightAssistant.Domain.Tests/Flights/FlightTests.cs`
- Test: `FlightAssistant.Infrastructure.Tests/Persistance/Repositories/FlightRepositoryTests.cs`

**Interfaces:**
- Consumes: `Flight.DelayThreshold` from Task 1.
- Produces:
  - `Flight.DelayNotifiedDepartureUtc` (`DateTime?`).
  - `public const string Flight.DelayedStatusCode = "DLY"`.
  - `Flight.ApplyStatusUpdate(string statusCode, DateTime? estimatedDepartureUtc, DateTime? estimatedArrivalUtc, DateTime? actualDepartureUtc, DateTime? actualArrivalUtc, string? equipmentAircraftIcao, DateTime nowUtc, bool statusChanged = true)`.
  - `FlightStatusChanged(..., DateTime OccurredUtc, bool ShouldNotify = true, int? DelayMinutes = null)`.

Task 3 consumes all four.

- [ ] **Step 1: Write the failing Domain tests.** In `FlightTests.cs`, in `FlightTests`:
- Add `using FlightAssistant.Domain.Flights.Events;` if it's missing.
- Add at the end of the class. `NewFlight()` departs at `Now + 1 h`.

```csharp
        // ── Push decision (#352) ──

        private static FlightStatusChanged SingleEvent(Flight flight) =>
            Assert.IsType<FlightStatusChanged>(Assert.Single(flight.DomainEvents));

        [Fact]
        public void ApplyStatusUpdate_AChangedStatus_ShouldNotify()
        {
            var flight = NewFlight();

            flight.ApplyStatusUpdate("DEP", null, null, Now, null, null, Now, statusChanged: true);

            var evt = SingleEvent(flight);
            Assert.True(evt.ShouldNotify);
            Assert.Null(evt.DelayMinutes);
        }

        [Fact]
        public void ApplyStatusUpdate_TheSameStatusAgain_DoesNotNotify()
        {
            // A corrected actual departure on DEP records the event without a second push.
            var flight = NewFlight();

            flight.ApplyStatusUpdate("DEP", null, null, Now.AddMinutes(2), null, null, Now, statusChanged: false);

            Assert.False(SingleEvent(flight).ShouldNotify);
        }

        [Fact]
        public void ApplyStatusUpdate_AFirstDelay_NotifiesWithTheMinutesLate_AndRemembersTheEstimate()
        {
            var flight = NewFlight();

            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(40), null, null, null, null, Now, statusChanged: true);

            var evt = SingleEvent(flight);
            Assert.True(evt.ShouldNotify);
            Assert.Equal(40, evt.DelayMinutes);
            Assert.Equal(Now.AddHours(1).AddMinutes(40), flight.DelayNotifiedDepartureUtc);
        }

        [Theory]
        [InlineData(54, false)] // 14 min later than the last push: not enough
        [InlineData(55, true)]  // exactly 15 min later
        [InlineData(30, false)] // the estimate moved earlier
        public void ApplyStatusUpdate_AStillDelayedFlight_NotifiesOnlyWhenTheDelayGrewByTheThreshold(int minutesLate, bool expectNotify)
        {
            var flight = NewFlight();
            var pushedFor = Now.AddHours(1).AddMinutes(40);
            flight.ApplyStatusUpdate("DLY", pushedFor, null, null, null, null, Now, statusChanged: true);
            flight.ClearDomainEvents();

            var estimate = Now.AddHours(1).AddMinutes(minutesLate);
            flight.ApplyStatusUpdate("DLY", estimate, null, null, null, null, Now.AddMinutes(5), statusChanged: false);

            var evt = SingleEvent(flight);
            Assert.Equal(expectNotify, evt.ShouldNotify);
            Assert.Equal(minutesLate, evt.DelayMinutes);
            Assert.Equal(expectNotify ? estimate : pushedFor, flight.DelayNotifiedDepartureUtc);
        }

        [Fact]
        public void ApplyStatusUpdate_SmallSlips_AddUpToAnotherPush()
        {
            // 40 → 50 → 60 min late: neither slip is 15 min, but 60 is 20 min past the last push.
            var flight = NewFlight();
            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(40), null, null, null, null, Now, statusChanged: true);
            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(50), null, null, null, null, Now.AddMinutes(5), statusChanged: false);
            flight.ClearDomainEvents();

            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(60), null, null, null, null, Now.AddMinutes(10), statusChanged: false);

            Assert.True(SingleEvent(flight).ShouldNotify);
        }

        [Fact]
        public void ApplyStatusUpdate_AFlightDelayedBeforeThisShipped_IsPushedOnce()
        {
            // Still DLY (no status change) but no remembered estimate: the next delayed sync pushes once.
            var flight = NewFlight();

            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(40), null, null, null, null, Now, statusChanged: false);
            Assert.True(SingleEvent(flight).ShouldNotify);
            flight.ClearDomainEvents();

            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(45), null, null, null, null, Now.AddMinutes(5), statusChanged: false);
            Assert.False(SingleEvent(flight).ShouldNotify);
        }

        [Fact]
        public void ApplyStatusUpdate_ADelayWithoutAnEstimate_NotifiesOnlyOnAStatusChange_WithoutMinutes()
        {
            var flight = NewFlight();

            flight.ApplyStatusUpdate("DLY", null, null, null, null, null, Now, statusChanged: false);

            var evt = SingleEvent(flight);
            Assert.False(evt.ShouldNotify);
            Assert.Null(evt.DelayMinutes);
            Assert.Null(flight.DelayNotifiedDepartureUtc);
        }

        [Fact]
        public void ApplyStatusUpdate_ReturningToDelayed_Notifies_EvenWithinTheThreshold()
        {
            // DLY (40) → SCH → DLY (45): back to DLY is a status change, so it pushes.
            var flight = NewFlight();
            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(40), null, null, null, null, Now, statusChanged: true);
            flight.ApplyStatusUpdate("SCH", Now.AddHours(1), null, null, null, null, Now.AddMinutes(5), statusChanged: true);
            flight.ClearDomainEvents();

            flight.ApplyStatusUpdate("DLY", Now.AddHours(1).AddMinutes(45), null, null, null, null, Now.AddMinutes(10), statusChanged: true);

            Assert.True(SingleEvent(flight).ShouldNotify);
            Assert.Equal(Now.AddHours(1).AddMinutes(45), flight.DelayNotifiedDepartureUtc);
        }
```

- [ ] **Step 2: Write the failing PostgreSQL round-trip test.** In `FlightRepositoryTests.cs`, add at the end of the class:

```csharp
    [Fact]
    public async Task DelayNotifiedDepartureUtc_IsStoredAndReadBack()
    {
        using (var db = CreateContext())
        {
            var flight = FlightArriving(db, "TK40", SyncNow.AddHours(8));
            flight.ApplyStatusUpdate("DLY", flight.ScheduledDepartureUtc.AddMinutes(40), null, null, null, null, SyncNow, statusChanged: true);
            db.SaveChanges();
        }

        using var readDb = CreateContext();
        var stored = await readDb.Flights.AsNoTracking().SingleAsync(f => f.FlightNumber == "TK40");

        Assert.Equal(stored.ScheduledDepartureUtc.AddMinutes(40), stored.DelayNotifiedDepartureUtc);
    }
```

(Add `using Microsoft.EntityFrameworkCore;` if it's missing.)

- [ ] **Step 3: Run the tests and watch them fail.**

Run: `dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj --filter "FullyQualifiedName~FlightTests"`
Expected: build FAILS, because `statusChanged`, `ShouldNotify`, `DelayMinutes` and `DelayNotifiedDepartureUtc` don't exist.

- [ ] **Step 4: Extend the event.** Replace the record in `FlightStatusChanged.cs` with:

```csharp
    /// <summary>
    /// Raised by <see cref="Entities.Flight.ApplyStatusUpdate"/> when a new status event
    /// is applied to a flight. Handled by the push notification handler after SaveChanges
    /// succeeds via the DomainEventDispatchInterceptor.
    /// </summary>
    /// <param name="ShouldNotify">Whether travelers should get a push (#352): the status changed, or a
    /// delay grew by at least <c>Flight.DelayThreshold</c> since the last delay push.</param>
    /// <param name="DelayMinutes">For a delay with an estimated departure after the schedule, how many
    /// minutes late the estimate is; otherwise null.</param>
    public sealed record FlightStatusChanged(
        int FlightId,
        string FlightNumber,
        DateOnly ServiceDateUtc,
        string StatusCode,
        DateTime OccurredUtc,
        bool ShouldNotify = true,
        int? DelayMinutes = null
    ) : DomainEvent(OccurredUtc);
```

- [ ] **Step 5: Decide the push in the flight.** In `Flight.cs`:

(a) Directly below `public const string CancelledStatusCode = "CAN";`, add:

```csharp
        public const string DelayedStatusCode = "DLY";
```

(b) Directly after the `CancelledAtUtc` property, add:

```csharp

        /// <summary>The estimated departure the last delay push went out for (#352). A flight that stays
        /// delayed is pushed again only when its estimate moves at least <see cref="DelayThreshold"/> later
        /// than this, so small slips add up. Never cleared: a return to delayed is a status change and pushes
        /// anyway.</summary>
        public DateTime? DelayNotifiedDepartureUtc { get; private set; }
```

(c) Replace the whole `ApplyStatusUpdate` method with the version below. Add the summary if it has none, or replace the one it has.

```csharp
        /// <summary>
        /// Records a new status from the provider and raises <see cref="FlightStatusChanged"/>, which the
        /// push handler acts on. The event says whether a push should go out (#352): when the status
        /// changed (<paramref name="statusChanged"/>), or, for a flight that stays delayed, once its
        /// estimated departure is at least <see cref="DelayThreshold"/> later than the last delay push's.
        /// A correction to an existing status (a revised actual departure, a small slip) records the event
        /// without a push. For a delay, the event carries how many minutes late the estimate is.
        /// </summary>
        public void ApplyStatusUpdate(
            string statusCode,
            DateTime? estimatedDepartureUtc,
            DateTime? estimatedArrivalUtc,
            DateTime? actualDepartureUtc,
            DateTime? actualArrivalUtc,
            string? equipmentAircraftIcao,
            DateTime nowUtc,
            bool statusChanged = true)
        {
            ApplyProviderTimes(estimatedDepartureUtc, estimatedArrivalUtc, actualDepartureUtc, actualArrivalUtc, equipmentAircraftIcao, nowUtc);
            CancelledAtUtc = statusCode == CancelledStatusCode ? CancelledAtUtc ?? nowUtc : null;
            UpdatedUtc = nowUtc;

            var shouldNotify = statusChanged;
            int? delayMinutes = null;
            if (statusCode == DelayedStatusCode && estimatedDepartureUtc is { } estimate)
            {
                if (estimate > ScheduledDepartureUtc)
                    delayMinutes = (int)Math.Round((estimate - ScheduledDepartureUtc).TotalMinutes);

                shouldNotify = shouldNotify
                               || DelayNotifiedDepartureUtc is null
                               || estimate >= DelayNotifiedDepartureUtc.Value + DelayThreshold;

                if (shouldNotify)
                    DelayNotifiedDepartureUtc = estimate;
            }

            Raise(new FlightStatusChanged(Id, FlightNumber, ServiceDateUtc, statusCode, nowUtc, shouldNotify, delayMinutes));
        }
```

- [ ] **Step 6: Expose the column in the data browser.** In `BrowsableTables.cs`, in the `flights` column list, directly after `nameof(Flight.CancelledAtUtc),`, add:

```csharp
                nameof(Flight.DelayNotifiedDepartureUtc),
```

- [ ] **Step 7: Generate the migration.** From the worktree root, run:

```bash
CONNECTION_STRING="Host=localhost;Database=design-time" dotnet ef migrations add AddFlightDelayNotifiedDeparture --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
```

- **Check it.** The generated `Up` must contain exactly one `migrationBuilder.AddColumn<DateTime>(name: "DelayNotifiedDepartureUtc", table: "flights", type: "timestamp with time zone", nullable: true)`, and `Down` its `DropColumn`.
- **Stop condition.** If the migration contains anything else (other tables, columns or indexes), delete the three generated or modified migration files, restore the snapshot with `git checkout -- FlightAssistant.Infrastructure/Persistance/Migrations/FlightAssistantDbContextModelSnapshot.cs`, and report BLOCKED with the extra operations.

- [ ] **Step 8: Run the tests and watch them pass.**

Run:
- `dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj`
- `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FlightRepositoryTests|FullyQualifiedName~BrowsableTables"`

Expected: all PASS, including the `BrowsableTables` model-completeness tests (the new column is classified) and every existing `ApplyStatusUpdate_…` test.

- [ ] **Step 9: Prove the tests.** Make each break, run the named test, see it fail, then restore with `mv <file>.bak <file> && touch <file>`.
1. Delete `|| DelayNotifiedDepartureUtc is null`. `ApplyStatusUpdate_AFlightDelayedBeforeThisShipped_IsPushedOnce` fails.
2. Change `estimate >= DelayNotifiedDepartureUtc.Value + DelayThreshold` to `>`. The `55, true` row of `…_NotifiesOnlyWhenTheDelayGrewByTheThreshold` fails.
3. Remember every estimate, not just the pushed one: change `if (shouldNotify) DelayNotifiedDepartureUtc = estimate;` to `DelayNotifiedDepartureUtc = estimate;`. `ApplyStatusUpdate_SmallSlips_AddUpToAnotherPush` fails, because 60 min is only 10 past 50.
4. Delete `if (shouldNotify) DelayNotifiedDepartureUtc = estimate;`. `ApplyStatusUpdate_AFirstDelay_…` and `DelayNotifiedDepartureUtc_IsStoredAndReadBack` fail.
5. Remove `nameof(Flight.DelayNotifiedDepartureUtc),` from `BrowsableTables`. The model-completeness test fails.

- [ ] **Step 10: Build.** Run `dotnet build --configuration Release`. Expected: 0 errors, 0 warnings.

- [ ] **Step 11: Commit.**

```bash
git add FlightAssistant.Domain/Flights/Entities/Flight.cs \
  FlightAssistant.Domain/Flights/Events/FlightStatusChanged.cs \
  FlightAssistant.Application/Administration/BrowsableTables.cs \
  FlightAssistant.Infrastructure/Persistance/Migrations/*_AddFlightDelayNotifiedDeparture.cs \
  FlightAssistant.Infrastructure/Persistance/Migrations/*_AddFlightDelayNotifiedDeparture.Designer.cs \
  FlightAssistant.Infrastructure/Persistance/Migrations/FlightAssistantDbContextModelSnapshot.cs \
  FlightAssistant.Domain.Tests/Flights/FlightTests.cs \
  FlightAssistant.Infrastructure.Tests/Persistance/Repositories/FlightRepositoryTests.cs
git commit -m "feat: a status update pushes only on a status change or a delay that grew 15 minutes since the last delay push (#352)"
```

(Use the real generated file names in `git add`. End the message with a blank line and your attribution trailer.)

---

### Task 3: The sync and the push handler use the decision

**Files:**
- Modify: `FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs`
- Modify: `FlightAssistant.Application/EventHandlers/FlightStatusSyncedEventHandler.cs` (class `FlightStatusChangedNotificationHandler`)
- Modify: `CLAUDE.md`
- Test: `FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs`
- Test: `FlightAssistant.Application.Tests/EventHandlers/FlightStatusChangedNotificationHandlerTests.cs`

**Interfaces:**
- Consumes (from Task 2):
  - `Flight.ApplyStatusUpdate(..., DateTime nowUtc, bool statusChanged = true)`;
  - `FlightStatusChanged.ShouldNotify` and `FlightStatusChanged.DelayMinutes`.
- Also consumes the existing `IFlightRepository.GetLatestFlightStatusEventAsync(int, CancellationToken)` (#351).
- Produces: none (the last task).

- [ ] **Step 1: Write the failing sync handler tests.** In `SyncFlightStatusCommandHandlerTests.cs`, directly after `NewStatus_StillRaisesFlightStatusChanged`, add the tests below. `DEP` resolves to status id 2 and `SCH` to 1; the handler reads the latest event by `flight.Id`.

```csharp
    [Fact]
    public async Task NewStatus_TheSameStatusAsTheLatestEvent_RecordsIt_WithoutAPush()
    {
        // A corrected actual departure: DEP again at a new effective time (#352).
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(Status("DEP", effectiveAtUtc: Now.AddMinutes(2)));
        _flights.GetLatestFlightStatusEventAsync(flight.Id, Arg.Any<CancellationToken>())
            .Returns(FlightStatusEvent.Create(flight.Id, 2, Now, "TestProvider", "ext-1", "A1", "T5"));

        Assert.True(await Sync());

        await _flights.Received(1).AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        Assert.False(Assert.IsType<FlightStatusChanged>(Assert.Single(flight.DomainEvents)).ShouldNotify);
    }

    [Fact]
    public async Task NewStatus_ADifferentStatusFromTheLatestEvent_Notifies()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(Status("DEP"));
        _flights.GetLatestFlightStatusEventAsync(flight.Id, Arg.Any<CancellationToken>())
            .Returns(FlightStatusEvent.Create(flight.Id, 1, Now.AddMinutes(-30), "TestProvider", "ext-1"));

        Assert.True(await Sync());

        Assert.True(Assert.IsType<FlightStatusChanged>(Assert.Single(flight.DomainEvents)).ShouldNotify);
    }
```

In `NewStatus_StillRaisesFlightStatusChanged`, change its last line to also check the flag:

```csharp
        Assert.True(Assert.IsType<FlightStatusChanged>(Assert.Single(flight.DomainEvents)).ShouldNotify);
```

- [ ] **Step 2: Write the failing push handler tests.** In `FlightStatusChangedNotificationHandlerTests.cs`, add:

```csharp
    [Fact]
    public async Task Handle_AnEventThatShouldNotNotify_SendsNothing_AndLooksNothingUp()
    {
        await _handler.Handle(TestEvent with { StatusCode = "DEP", ShouldNotify = false }, default);

        await _flights.DidNotReceive().GetTrackingsByFlightIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _push.DidNotReceive().SendAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ADelayWithMinutes_SaysHowLate()
    {
        var travelerId = Guid.NewGuid();
        var tracking = TravelerFlight.Create(travelerId, 1);
        tracking.SetNotify(true);
        var traveler = Traveler.Register(travelerId, "test@example.com");
        traveler.AddDeviceToken("ios", "token-aaa-111");
        _flights.GetTrackingsByFlightIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<TravelerFlight> { tracking });
        _travelers.GetByIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Traveler> { traveler });

        await _handler.Handle(TestEvent with { StatusCode = "DLY", DelayMinutes = 40 }, default);

        await _push.Received(1).SendAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), "Your flight is delayed by 40 min.", Arg.Any<CancellationToken>());
    }
```

The existing `[InlineData("DLY", "Your flight is delayed.")]` row stays: its event has no `DelayMinutes`.

- [ ] **Step 3: Run the tests and watch them fail.**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests|FullyQualifiedName~FlightStatusChangedNotificationHandlerTests"`

Expected:
- **Fail:**
  - `NewStatus_TheSameStatusAsTheLatestEvent_RecordsIt_WithoutAPush`, because `ShouldNotify` is true;
  - `Handle_AnEventThatShouldNotNotify_…`;
  - `Handle_ADelayWithMinutes_SaysHowLate`.
- **Pass already, as guards:** `NewStatus_ADifferentStatusFromTheLatestEvent_Notifies` and the updated `NewStatus_StillRaisesFlightStatusChanged`. Record them as "passes before, guard".

- [ ] **Step 4: Pass whether the status changed.** In `SyncFlightStatusCommand.cs`, in `ApplyLiveStatusAsync`:

(a) Replace

```csharp
            var evt = FlightStatusEvent.Create(
```

with

```csharp
            // The flight's current status is its most recently recorded event (#351). A push goes out only
            // when the status changes, or a delay grows (#352): a corrected actual departure, or a delay
            // slipping a few minutes, records the event without another push. Read before the flight changes.
            var latest = await _flights.GetLatestFlightStatusEventAsync(flight.Id, ct);

            var evt = FlightStatusEvent.Create(
```

(b) Replace the `flight.ApplyStatusUpdate(` call that follows:

```csharp
            flight.ApplyStatusUpdate(
                resolvedStatusCode,
                result.EstimatedDepartureUtc,
                result.EstimatedArrivalUtc,
                result.ActualDepartureUtc,
                result.ActualArrivalUtc,
                result.EquipmentIcao,
                now);
```

with

```csharp
            flight.ApplyStatusUpdate(
                resolvedStatusCode,
                result.EstimatedDepartureUtc,
                result.EstimatedArrivalUtc,
                result.ActualDepartureUtc,
                result.ActualArrivalUtc,
                result.EquipmentIcao,
                now,
                statusChanged: latest?.StatusId != statusId.Value);
```

- [ ] **Step 5: Skip and word pushes.** In `FlightStatusSyncedEventHandler.cs`:

(a) Replace

```csharp
            // Find all travelers tracking this flight with notifications enabled
            var body = GetStatusMessage(notification.StatusCode);
```

with

```csharp
            // A correction, or a delay that grew less than the threshold, records the event but sends
            // nothing (#352).
            if (!notification.ShouldNotify)
            {
                _logger.LogInformation(
                    "Skipping push for flight {FlightNumber}: status {Status} didn't change enough to notify",
                    notification.FlightNumber, notification.StatusCode);
                return;
            }

            // Find all travelers tracking this flight with notifications enabled
            var body = GetStatusMessage(notification.StatusCode, notification.DelayMinutes);
```

(b) Replace the `GetStatusMessage` method with:

```csharp
        private static string? GetStatusMessage(string statusCode, int? delayMinutes) => statusCode switch
        {
            "DEP" => "Your flight has departed.",
            "ARR" => "Your flight has landed.",
            // Minutes late rather than a clock time, so the message needs no timezone or locale (#352).
            "DLY" => delayMinutes is { } minutes ? $"Your flight is delayed by {minutes} min." : "Your flight is delayed.",
            "CAN" => "Your flight has been cancelled.",
            "DIV" => "Your flight has been diverted.",
            _ => null
        };
```

- [ ] **Step 6: Run the tests and watch them pass.**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests|FullyQualifiedName~FlightStatusChangedNotificationHandlerTests"`
Expected: all PASS.

- [ ] **Step 7: Prove the tests.** Make each break, run the named test, see it fail, then restore with `mv … .bak` and `touch`.
1. Pass `statusChanged: true` instead of the comparison. `NewStatus_TheSameStatusAsTheLatestEvent_RecordsIt_WithoutAPush` fails.
2. Pass `statusChanged: latest?.StatusId == statusId.Value`. `NewStatus_ADifferentStatusFromTheLatestEvent_Notifies` and `NewStatus_StillRaisesFlightStatusChanged` fail.
3. Delete the `if (!notification.ShouldNotify) { … }` block. `Handle_AnEventThatShouldNotNotify_SendsNothing_AndLooksNothingUp` fails.
4. Ignore `delayMinutes` in the `DLY` branch (always `"Your flight is delayed."`). `Handle_ADelayWithMinutes_SaysHowLate` fails.

- [ ] **Step 8: Document it in `CLAUDE.md`.** Directly after the paragraph that starts `**A status the flight already has still updates it (#351).**`, add:

```markdown
**A push goes out when the status changes, or a delay grows (#352).** `AeroApiFlightStatusProvider` decides a delay from the times before departure. With both `scheduled_out` and `estimated_out`, a scheduled or delayed flight is `DLY` when the estimate is at least `Flight.DelayThreshold` (15 min) late and `SCH` otherwise, whatever the prose says: AeroAPI writes a delay both as a qualifier (`"Scheduled / Delayed"`) and as a leading `"Delayed"`. Without both times, a `"Delayed"` segment still means `DLY`. `"Taxiing"` maps to `DEP`. `SyncFlightStatusCommandHandler` passes `statusChanged` to `Flight.ApplyStatusUpdate` by comparing the new status id with the flight's latest event (#351's most recently recorded). `FlightStatusChanged.ShouldNotify` is true on a status change, or, while a flight stays `DLY`, once its estimated departure is at least `DelayThreshold` later than `Flight.DelayNotifiedDepartureUtc`, the estimate the last delay push went out for. It's stored in the same save as the event, so small slips add up. A corrected `actual_out` on `DEP`, or a delay moving earlier, records the event without a push. `FlightStatusChangedNotificationHandler` skips an event whose `ShouldNotify` is false before any lookup, and a delay push says how late the flight is ("Your flight is delayed by 40 min.") — minutes, so the message needs no timezone or locale. A flight that was already delayed when this shipped has no stored estimate, so its next delayed sync pushes once. The known-status path (#351) never pushes.
```

- [ ] **Step 9: Build and run everything.**
- Run `dotnet build --configuration Release`. Expected: 0 errors, 0 warnings.
- Run the full `dotnet test` once (Docker is running) and record the per-project counts.
- Confirm `spec/openapi.json` is unchanged: run `FlightSync__Enabled=false scripts/generate-openapi.sh`, then `git diff --stat spec/openapi.json` should print nothing.

- [ ] **Step 10: Commit.**

```bash
git add FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs \
  FlightAssistant.Application/EventHandlers/FlightStatusSyncedEventHandler.cs \
  CLAUDE.md \
  FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs \
  FlightAssistant.Application.Tests/EventHandlers/FlightStatusChangedNotificationHandlerTests.cs
git commit -m "fix: push only on a real status change or a growing delay, and say how many minutes late (#352)"
```

(End the message with a blank line and your attribution trailer.)
