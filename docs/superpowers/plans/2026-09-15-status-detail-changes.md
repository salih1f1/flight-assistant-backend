# Status Detail Changes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A status sync that reports a status the flight already has no longer throws the response away. Its times update the flight, and a newly reported gate, terminal or baggage claim is recorded as a new event. Neither sends a push (#351).

**Architecture:**
- **Domain.**
  - `Flight.ApplyProviderTimes(...)` takes the provider's estimates, actuals and aircraft type without raising `FlightStatusChanged`. `ApplyStatusUpdate` now delegates its time handling to it.
  - `FlightStatusEvent.CreateDetailChange(...)` records a detail change under its own hash, `ComputeDetailChangeHash`, which includes the id of the event it follows. It's needed because `(FlightId, Source, DedupHash)` is unique and the status hash is already stored.
- **Infrastructure.**
  - `IFlightRepository.GetLatestFlightStatusEventAsync(flightId)` returns the highest-id event.
  - Both event lists order by effective time, then id, newest first, so a detail change sorts above the status event it shares a time with.
- **Application.** When the status hash already exists, `SyncFlightStatusCommandHandler` sends the response to `ApplyKnownStatusAsync` instead of returning early.

**Tech Stack:** .NET 10, EF Core + Npgsql, MediatR, xUnit, NSubstitute, Testcontainers PostgreSQL (#295).

**Spec:** GitHub issue #351, and the cost plan `docs/superpowers/plans/2026-09-15-flight-data-cost-reduction.md`, Phase 2's prerequisite ("#351, where gate, terminal, baggage and ETA changes are dropped as duplicates. Otherwise alert-delivered gate changes get discarded").

## Global Constraints

- Work only in the worktree `/Users/salihalishov/Repositories/flight-assistant-backend-351`, branch `feature/351-status-detail-changes`. Never touch the main checkout or other worktrees.
- **The status hash formula doesn't change.** `FlightStatusEvent.ComputeDedupHash(flightId, statusId, effectiveAt, source, externalId)` keeps its exact formula. `ComputeDedupHash(1, 2, 2025-06-15T10:00:00Z, "AeroAPI", "ext-1")` must stay `47E102A0F36D99F2`. A changed formula would stop every stored hash from matching, and each tracked flight would record, and push, its latest status again.
- **The detail-change hash.**
  - Signature: `FlightStatusEvent.ComputeDetailChangeHash(int flightId, int statusId, DateTime effectiveAt, string source, string? externalId, long previousEventId, string? gate, string? terminal, string? baggageClaim)`.
  - It hashes the status identity, a `detail` marker, the previous event's id and the three details. It's unique per (previous event, details) under the unique `(FlightId, Source, DedupHash)` index.
- **The rule when the status hash already exists** (a known status):
  - **Times.** The flight takes the provider's times through `ApplyProviderTimes`. It raises no event, leaves `CancelledAtUtc` alone, and moves `UpdatedUtc` only when something changed.
  - **The latest event** is the flight's highest-`Id` `FlightStatusEvent`.
  - **A new detail.** A detail counts as new only when the provider reports a non-null gate, terminal or baggage claim that differs from the latest event's.
  - **Recording.** If any detail is new, record `CreateDetailChange` with each detail set to the reported value, else the latest event's value. The sync returns `true`.
  - **Otherwise** nothing is recorded and the sync returns `false`.
  - **No push either way.** `FlightStatusChanged` is never raised on this path.
  - **Safe ordering.** The flight is changed only after the step that can throw (the latest-event read), which follows the handler's existing principle.
- **A new status is unchanged.** It still records `FlightStatusEvent.Create` and calls `ApplyStatusUpdate`, which raises `FlightStatusChanged`.
- **Event ordering.** `GetFlightStatusEventsAsync` and `GetRecentFlightStatusEventsAsync` order by `EffectiveAtUtc` descending, then `Id` descending.
- **API and schema.** No migration, no DTO, route or schema change, and no `spec/openapi.json` change.
- **No paid calls.** Make no call to AeroAPI or any other paid external API. Never run the API with sync on. Never run `dotnet ef database update`.
- **SQL tests.** Behaviour that depends on SQL (the unique index, ordering) is proved against PostgreSQL through the Testcontainers harness (#295).
- **The build stays warning-free** (#301).
- **TDD.** Every new test is seen failing first, except the formula characterization test, which must pass before any production change. Each task ends with a prove step: break the code on purpose, watch the named test fail, then restore. After restoring from a `.bak`, `touch` the file so MSBuild recompiles.
- **Commits.** Commit with explicit paths only (never `-A` or `.`). Every commit message ends with a blank line and the attribution trailer your own instructions require. Never push.

## Decisions made while planning

These are recorded so review doesn't relitigate them.

- **A separate hash, not a new formula.**
  - The unique `(FlightId, Source, DedupHash)` index refuses a second event with the status hash, so a detail change needs a different one.
  - Adding the details to `ComputeDedupHash` would make every stored hash stale. Each flight would record and push its current status again on the next sync, which is the one-time duplicate already accepted once, at the provider switch (#280). It isn't worth repeating.
- **The previous event's id is in the detail hash.** Without it, a gate that changes back (A1 → B7 → A1 → B7) would repeat a hash and hit the unique index.
- **A detail the provider stops reporting keeps its last known value.** AeroAPI omits fields it no longer has. Treating a missing gate as a change would record an event that blanks the gate travelers already saw.
- **No push for a detail change or a time change.**
  - #351 leaves "should a gate change notify" to a separate product story.
  - #352 separately reworks when status pushes are sent (it plans a `PreviousStatusCode` on `FlightStatusChanged`). This plan doesn't touch `FlightStatusChanged` or the push handler, so the two don't overlap.
- **Times are overwritten as reported, as `ApplyStatusUpdate` already does,** including a missing estimate clearing the stored one, so both paths agree. The aircraft type updates only when it's reported.
- **"Latest" means recorded last (highest id), not latest effective time,** because a detail change is compared with what the flight most recently stored.
- **Accepted:** two concurrent syncs recording the identical detail change can both pass the check, and the second insert fails on the unique index. That's the same pre-existing race as two concurrent status events.

## Commands

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-351
dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj --filter "FullyQualifiedName~FlightTests|FullyQualifiedName~FlightStatusEventTests"
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FlightRepositoryTests"
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests"
dotnet build --configuration Release
```

---

### Task 1: Times without a push, detail-change events, and the latest event

**Files:**
- Modify: `FlightAssistant.Domain/Flights/Entities/Flight.cs`
- Modify: `FlightAssistant.Domain/Flights/Entities/FlightStatusEvent.cs`
- Modify: `FlightAssistant.Application/Interfaces/Repositories/IFlightRepository.cs`
- Modify: `FlightAssistant.Infrastructure/Persistance/Repositories/FlightRepository.cs`
- Test: `FlightAssistant.Domain.Tests/Flights/FlightTests.cs` (the `FlightTests` and `FlightStatusEventTests` classes)
- Test: `FlightAssistant.Infrastructure.Tests/Persistance/Repositories/FlightRepositoryTests.cs`

**Interfaces:**
- Produces:
  - `Flight.ApplyProviderTimes(DateTime? estimatedDepartureUtc, DateTime? estimatedArrivalUtc, DateTime? actualDepartureUtc, DateTime? actualArrivalUtc, string? equipmentAircraftIcao, DateTime nowUtc)`.
  - `FlightStatusEvent.CreateDetailChange(int flightId, int statusId, DateTime effectiveAtUtc, string source, string? externalId, long previousEventId, string? gate, string? terminal, string? baggageClaim, string? rawPayloadJson = null)`.
  - `FlightStatusEvent.ComputeDetailChangeHash(int flightId, int statusId, DateTime effectiveAt, string source, string? externalId, long previousEventId, string? gate, string? terminal, string? baggageClaim)`.
  - `IFlightRepository.GetLatestFlightStatusEventAsync(int flightId, CancellationToken ct = default)` returning `Task<FlightStatusEvent?>`.

Task 2 consumes all four.

- [ ] **Step 1: Pin the status hash formula before changing anything.** In `FlightTests.cs`, in `FlightStatusEventTests`, add:

```csharp
        [Fact]
        public void ComputeDedupHash_FormulaIsUnchanged()
        {
            // Every stored hash depends on this formula (#351). Changing it would re-record, and push,
            // every tracked flight's latest status on its next sync.
            var at = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);

            Assert.Equal("47E102A0F36D99F2", FlightStatusEvent.ComputeDedupHash(1, 2, at, "AeroAPI", "ext-1"));
        }
```

Run: `dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj --filter "FullyQualifiedName~ComputeDedupHash_FormulaIsUnchanged"`
Expected: PASS against the unchanged code. If it fails, stop and report the actual value. Don't change the formula or the literal.

- [ ] **Step 2: Write the failing Domain tests.**

(a) In `FlightTests` (the class that already uses `Now`), add:

```csharp
        [Fact]
        public void ApplyProviderTimes_UpdatesTheTimes_RaisesNoEvent_AndLeavesCancelledAtUtc()
        {
            var flight = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(1), Now.AddHours(8));
            flight.ApplyStatusUpdate("CAN", null, null, null, null, null, Now);
            flight.ClearDomainEvents();

            flight.ApplyProviderTimes(Now.AddHours(2), Now.AddHours(9), null, null, "A321", Now.AddMinutes(5));

            Assert.Equal(Now.AddHours(2), flight.EstimatedDepartureUtc);
            Assert.Equal(Now.AddHours(9), flight.EstimatedArrivalUtc);
            Assert.Equal("A321", flight.EquipmentAircraftIcao);
            Assert.Equal(Now, flight.CancelledAtUtc);
            Assert.Equal(Now.AddMinutes(5), flight.UpdatedUtc);
            Assert.Empty(flight.DomainEvents);
        }

        [Fact]
        public void ApplyProviderTimes_NothingChanged_LeavesUpdatedUtc_AndAMissingAircraftTypeKeepsTheStoredOne()
        {
            var flight = Flight.Create(1, "BA123", 10, 20, DateOnly.FromDateTime(Now), Now.AddHours(1), Now.AddHours(8), "B789");
            flight.ApplyStatusUpdate("DEP", Now.AddHours(1), Now.AddHours(8), Now.AddHours(1), null, "B789", Now);

            flight.ApplyProviderTimes(Now.AddHours(1), Now.AddHours(8), Now.AddHours(1), null, null, Now.AddMinutes(5));

            Assert.Equal(Now, flight.UpdatedUtc);
            Assert.Equal("B789", flight.EquipmentAircraftIcao);
        }
```

(b) In `FlightStatusEventTests`, add:

```csharp
        [Fact]
        public void CreateDetailChange_HashDiffersFromTheStatusHash_AndPerPreviousEvent()
        {
            // A gate that changes back must still get a new hash under the unique (FlightId, Source, DedupHash) index.
            var at = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var status = FlightStatusEvent.Create(1, 2, at, "AeroAPI", "ext-1", "A1", "T5");
            var toB7 = FlightStatusEvent.CreateDetailChange(1, 2, at, "AeroAPI", "ext-1", 10, "B7", "T5", null);
            var backToA1 = FlightStatusEvent.CreateDetailChange(1, 2, at, "AeroAPI", "ext-1", 11, "A1", "T5", null);
            var toB7Again = FlightStatusEvent.CreateDetailChange(1, 2, at, "AeroAPI", "ext-1", 12, "B7", "T5", null);

            Assert.Equal(4, new[] { status.DedupHash, toB7.DedupHash, backToA1.DedupHash, toB7Again.DedupHash }.Distinct().Count());
            Assert.Equal("B7", toB7.Gate);
            Assert.Equal("T5", toB7.Terminal);
            Assert.Equal(2, toB7.StatusId);
            Assert.Equal(at, toB7.EffectiveAtUtc);
        }

        [Fact]
        public void CreateDetailChange_StoresTheHashComputeDetailChangeHashGives()
        {
            var at = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var evt = FlightStatusEvent.CreateDetailChange(1, 2, at, "AeroAPI", "ext-1", 10, "B7", "T5", "7");

            Assert.Equal(FlightStatusEvent.ComputeDetailChangeHash(1, 2, at, "AeroAPI", "ext-1", 10, "B7", "T5", "7"), evt.DedupHash);
            Assert.Equal("7", evt.BaggageClaim);
        }
```

- [ ] **Step 3: Write the failing PostgreSQL tests.** In `FlightRepositoryTests.cs`, add at the end of the class:

```csharp
    // ── Status events: the latest one, and ties on effective time (#351) ──

    private static int StatusId(FlightAssistantDbContext db, string code) =>
        db.FlightStatus.Where(s => s.Code == code).Select(s => s.Id).Single();

    [Fact]
    public async Task ADetailChange_PassesTheUniqueIndex_IsTheLatestEvent_AndIsListedAboveItsStatusEvent()
    {
        using var db = CreateContext();
        var flight = FlightArriving(db, "TK30", SyncNow.AddHours(3));
        var departed = StatusId(db, "DEP");

        var status = FlightStatusEvent.Create(flight.Id, departed, SyncNow, "AeroAPI", "fa-1", "A1", "T5");
        db.FlightStatusEvents.Add(status);
        db.SaveChanges();
        var gateChange = FlightStatusEvent.CreateDetailChange(flight.Id, departed, SyncNow, "AeroAPI", "fa-1", status.Id, "B7", "T5", null);
        db.FlightStatusEvents.Add(gateChange);
        db.SaveChanges(); // same flight, source, status and effective time: only the separate hash lets this through

        var repo = Repository(db, ClockAt(SyncNow));

        Assert.Equal(gateChange.Id, (await repo.GetLatestFlightStatusEventAsync(flight.Id))!.Id);
        Assert.Equal(new[] { gateChange.Id, status.Id }, (await repo.GetFlightStatusEventsAsync(flight.Id)).Select(e => e.Id));
        Assert.Equal(gateChange.Id, Assert.Single(await repo.GetRecentFlightStatusEventsAsync(flight.Id, limit: 1)).Id);
    }

    [Fact]
    public async Task GetLatestFlightStatusEventAsync_AFlightWithNoEvents_ReturnsNull()
    {
        using var db = CreateContext();
        var flight = FlightArriving(db, "TK31", SyncNow.AddHours(3));

        Assert.Null(await Repository(db, ClockAt(SyncNow)).GetLatestFlightStatusEventAsync(flight.Id));
    }
```

(`FlightArriving`, `Repository`, `ClockAt` and `SyncNow` already exist in this class. Add `using FlightAssistant.Domain.Flights.Entities;` only if it's missing.)

- [ ] **Step 4: Run the tests and watch them fail.**

Run: `dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj`
Expected: build FAILS, because `ApplyProviderTimes`, `CreateDetailChange` and `ComputeDetailChangeHash` don't exist. The Infrastructure tests fail to build too, because `GetLatestFlightStatusEventAsync` doesn't exist.

- [ ] **Step 5: Add `ApplyProviderTimes`.** In `Flight.cs`, replace the whole `ApplyStatusUpdate` method with:

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
            ApplyProviderTimes(estimatedDepartureUtc, estimatedArrivalUtc, actualDepartureUtc, actualArrivalUtc, equipmentAircraftIcao, nowUtc);
            CancelledAtUtc = statusCode == CancelledStatusCode ? CancelledAtUtc ?? nowUtc : null;
            UpdatedUtc = nowUtc;

            Raise(new FlightStatusChanged(Id, FlightNumber, ServiceDateUtc, statusCode, nowUtc));
        }

        /// <summary>
        /// Takes the provider's times for a status the flight already has (#351). Estimates and actuals are
        /// taken as reported, so a missing one clears the stored value, as <see cref="ApplyStatusUpdate"/>
        /// does. The aircraft type is taken only when reported. Raises no event, so it sends no push, and
        /// leaves <see cref="CancelledAtUtc"/> alone, since that follows the status.
        /// <see cref="UpdatedUtc"/> moves only when something changed.
        /// </summary>
        public void ApplyProviderTimes(
            DateTime? estimatedDepartureUtc,
            DateTime? estimatedArrivalUtc,
            DateTime? actualDepartureUtc,
            DateTime? actualArrivalUtc,
            string? equipmentAircraftIcao,
            DateTime nowUtc)
        {
            var changed = EstimatedDepartureUtc != estimatedDepartureUtc
                          || EstimatedArrivalUtc != estimatedArrivalUtc
                          || ActualDepartureUtc != actualDepartureUtc
                          || ActualArrivalUtc != actualArrivalUtc
                          || (equipmentAircraftIcao is not null && EquipmentAircraftIcao != equipmentAircraftIcao);

            EstimatedDepartureUtc = estimatedDepartureUtc;
            EstimatedArrivalUtc = estimatedArrivalUtc;
            ActualDepartureUtc = actualDepartureUtc;
            ActualArrivalUtc = actualArrivalUtc;
            if (equipmentAircraftIcao is not null)
                EquipmentAircraftIcao = equipmentAircraftIcao;

            if (changed)
                UpdatedUtc = nowUtc;
        }
```

- [ ] **Step 6: Add the detail-change event.** In `FlightStatusEvent.cs`, directly after `ComputeDedupHash`, add:

```csharp

        /// <summary>
        /// A new gate, terminal or baggage claim for a status the flight already has, recorded after
        /// <paramref name="previousEventId"/> (#351). Its <see cref="DedupHash"/> comes from
        /// <see cref="ComputeDetailChangeHash"/>, not <see cref="ComputeDedupHash"/>: that status hash is
        /// already stored for the flight, and the unique (FlightId, Source, DedupHash) index would refuse it.
        /// </summary>
        public static FlightStatusEvent CreateDetailChange(
            int flightId,
            int statusId,
            DateTime effectiveAtUtc,
            string source,
            string? externalId,
            long previousEventId,
            string? gate,
            string? terminal,
            string? baggageClaim,
            string? rawPayloadJson = null)
        {
            var evt = Create(flightId, statusId, effectiveAtUtc, source, externalId, gate, terminal, baggageClaim,
                rawPayloadJson: rawPayloadJson);
            evt.DedupHash = ComputeDetailChangeHash(
                flightId, statusId, effectiveAtUtc, source, externalId, previousEventId, gate, terminal, baggageClaim);
            return evt;
        }

        /// <summary>
        /// The hash of a detail change: the status identity, a <c>detail</c> marker, the event it follows,
        /// and the details. The previous event keeps it unique when a gate changes back to an earlier value.
        /// <see cref="ComputeDedupHash"/>'s formula stays unchanged, so every stored hash stays valid.
        /// </summary>
        public static string ComputeDetailChangeHash(
            int flightId,
            int statusId,
            DateTime effectiveAt,
            string source,
            string? externalId,
            long previousEventId,
            string? gate,
            string? terminal,
            string? baggageClaim)
        {
            var raw = $"{flightId}|{statusId}|{effectiveAt:O}|{source}|{externalId}|detail|{previousEventId}|{gate}|{terminal}|{baggageClaim}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes)[..16];
        }
```

- [ ] **Step 7: Add the latest event, and break ties in the event lists.**

(a) In `IFlightRepository.cs`, directly after `GetRecentFlightStatusEventsAsync`, add:

```csharp

        /// <summary>The flight's most recently recorded status event (the highest id), or null when it has
        /// none (#351). Read-only (not tracked).</summary>
        Task<FlightStatusEvent?> GetLatestFlightStatusEventAsync(int flightId, CancellationToken ct = default);
```

(b) In `FlightRepository.cs`, directly after `GetRecentFlightStatusEventsAsync`, add:

```csharp

        public Task<FlightStatusEvent?> GetLatestFlightStatusEventAsync(int flightId, CancellationToken ct = default) =>
            _db.FlightStatusEvents
                .AsNoTracking()
                .Where(e => e.FlightId == flightId)
                .OrderByDescending(e => e.Id)
                .FirstOrDefaultAsync(ct);
```

(c) In the same file, in **both** `GetFlightStatusEventsAsync` and `GetRecentFlightStatusEventsAsync`, replace the line

```csharp
                .OrderByDescending(e => e.EffectiveAtUtc)
```

with

```csharp
                .OrderByDescending(e => e.EffectiveAtUtc)
                // A detail change shares its status event's effective time (#351); the newer one comes first.
                .ThenByDescending(e => e.Id)
```

It occurs exactly twice. Leave `GetLatestFlightStatusEventAsync`'s own ordering as it is.

- [ ] **Step 8: Run the tests and watch them pass.**

Run:
- `dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj`
- `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FlightRepositoryTests|FullyQualifiedName~AdminDrilldownRepositoryTests"`

Expected: all PASS, including every existing `ApplyStatusUpdate_…` test and the drill-down ordering tests.

- [ ] **Step 9: Prove the tests.** Make each break, run the named test, see it fail, then restore with `mv <file>.bak <file> && touch <file>`.
1. In `ComputeDetailChangeHash`, drop `|{previousEventId}` from `raw`. `CreateDetailChange_HashDiffersFromTheStatusHash_AndPerPreviousEvent` fails with 3, not 4.
2. In `CreateDetailChange`, delete the `evt.DedupHash = ComputeDetailChangeHash(...)` assignment. `ADetailChange_PassesTheUniqueIndex_…` fails with a unique-index `DbUpdateException`, and `CreateDetailChange_StoresTheHashComputeDetailChangeHashGives` fails.
3. In `GetFlightStatusEventsAsync`, delete the `.ThenByDescending(e => e.Id)` line. `ADetailChange_PassesTheUniqueIndex_…` fails on the list order. If PostgreSQL happens to return the tie newest-first anyway, record that and break it instead with `.ThenBy(e => e.Id)`, which must fail.
4. In `GetLatestFlightStatusEventAsync`, change `OrderByDescending(e => e.Id)` to `OrderBy(e => e.Id)`. `ADetailChange_PassesTheUniqueIndex_…` fails on the latest event.
5. In `ApplyProviderTimes`, add `Raise(new FlightStatusChanged(Id, FlightNumber, ServiceDateUtc, "SCH", nowUtc));`. `ApplyProviderTimes_UpdatesTheTimes_RaisesNoEvent_AndLeavesCancelledAtUtc` fails.
6. In `ApplyProviderTimes`, set `UpdatedUtc = nowUtc;` unconditionally. `ApplyProviderTimes_NothingChanged_…` fails.
7. In `ComputeDedupHash`, change a `|` separator to `/`. `ComputeDedupHash_FormulaIsUnchanged` fails.

- [ ] **Step 10: Build.** Run `dotnet build --configuration Release`. Expected: 0 errors, 0 warnings.

- [ ] **Step 11: Commit.**

```bash
git add FlightAssistant.Domain/Flights/Entities/Flight.cs \
  FlightAssistant.Domain/Flights/Entities/FlightStatusEvent.cs \
  FlightAssistant.Application/Interfaces/Repositories/IFlightRepository.cs \
  FlightAssistant.Infrastructure/Persistance/Repositories/FlightRepository.cs \
  FlightAssistant.Domain.Tests/Flights/FlightTests.cs \
  FlightAssistant.Infrastructure.Tests/Persistance/Repositories/FlightRepositoryTests.cs
git commit -m "feat: take provider times without a push, record detail changes under their own hash, and read a flight's latest event (#351)"
```

(End the message with a blank line and your attribution trailer.)

---

### Task 2: A sync that reports a known status keeps its changes

**Files:**
- Modify: `FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs`
- Modify: `CLAUDE.md`
- Test: `FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs`

**Interfaces:**
- Consumes (from Task 1): `Flight.ApplyProviderTimes(...)`, `FlightStatusEvent.CreateDetailChange(...)`, `FlightStatusEvent.ComputeDetailChangeHash(...)`, and `IFlightRepository.GetLatestFlightStatusEventAsync(int, CancellationToken)`.
- Produces: none (the last task).

- [ ] **Step 1: Write the failing handler tests.** In `SyncFlightStatusCommandHandlerTests.cs`:
- Add `using FlightAssistant.Domain.Flights.Events;` if it's missing.
- At the end of the class, add this section. The class's `Status(...)` helper reports gate `A1`, terminal `T5`, no baggage claim, aircraft `B789` and provider id `ext-1` at `Now`. `DEP` resolves to status id 2, and the provider's name is `"TestProvider"`.

```csharp
    // ── A status the flight already has (#351) ──

    /// <summary>The provider reports <paramref name="reported"/> for a status already stored for the flight,
    /// whose latest event has the given details.</summary>
    private Flight GivenKnownStatus(FlightStatusResult reported, string? gate = "A1", string? terminal = "T5",
        string? baggageClaim = null, DateTime? departureUtc = null)
    {
        var flight = Given(Departing(departureUtc ?? Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(reported);
        _flights.FlightStatusEventExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _flights.GetLatestFlightStatusEventAsync(flight.Id, Arg.Any<CancellationToken>())
            .Returns(FlightStatusEvent.Create(flight.Id, 2, Now, "TestProvider", "ext-1", gate, terminal, baggageClaim));
        return flight;
    }

    [Fact]
    public async Task NewStatus_StillRaisesFlightStatusChanged()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<StatusCallKind>(), Arg.Any<CancellationToken>())
            .Returns(Status());

        Assert.True(await Sync());

        Assert.Single(flight.DomainEvents, e => e is FlightStatusChanged);
    }

    [Fact]
    public async Task KnownStatus_ANewGate_RecordsADetailChange_WithNoPush()
    {
        var flight = GivenKnownStatus(Status() with { Gate = "B7" });

        Assert.True(await Sync());

        await _flights.Received(1).AddFlightStatusEventAsync(
            Arg.Is<FlightStatusEvent>(e =>
                e.Gate == "B7" && e.Terminal == "T5" && e.BaggageClaim == null
                && e.StatusId == 2 && e.EffectiveAtUtc == Now && e.Source == "TestProvider"
                && e.DedupHash == FlightStatusEvent.ComputeDetailChangeHash(flight.Id, 2, Now, "TestProvider", "ext-1", 0, "B7", "T5", null)),
            Arg.Any<CancellationToken>());
        Assert.DoesNotContain(flight.DomainEvents, e => e is FlightStatusChanged);
        Assert.Equal(Now, flight.LastSyncedUtc);
    }

    [Fact]
    public async Task KnownStatus_ABaggageClaimReportedLater_IsRecorded_KeepingDetailsNoLongerReported()
    {
        var flight = GivenKnownStatus(Status() with { Gate = null, BaggageClaim = "7" });

        Assert.True(await Sync());

        await _flights.Received(1).AddFlightStatusEventAsync(
            Arg.Is<FlightStatusEvent>(e => e.Gate == "A1" && e.Terminal == "T5" && e.BaggageClaim == "7"),
            Arg.Any<CancellationToken>());
        Assert.DoesNotContain(flight.DomainEvents, e => e is FlightStatusChanged);
    }

    [Fact]
    public async Task KnownStatus_ADetailNoLongerReported_RecordsNothing()
    {
        GivenKnownStatus(Status() with { Gate = null });

        Assert.False(await Sync());

        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KnownStatus_AnIdenticalResponse_RecordsNothing_AndLeavesTheFlightUnchanged()
    {
        var flight = GivenKnownStatus(Status() with { EquipmentIcao = null });
        var updatedBefore = flight.UpdatedUtc;

        Assert.False(await Sync());

        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        Assert.Equal(updatedBefore, flight.UpdatedUtc);
        Assert.Empty(flight.DomainEvents);
    }

    [Fact]
    public async Task KnownStatus_ANewEstimatedArrival_UpdatesTheFlight_WithNoEventOrPush()
    {
        var flight = GivenKnownStatus(Status() with { EstimatedArrivalUtc = Now.AddHours(9) });

        Assert.False(await Sync());

        Assert.Equal(Now.AddHours(9), flight.EstimatedArrivalUtc);
        await _flights.DidNotReceive().AddFlightStatusEventAsync(Arg.Any<FlightStatusEvent>(), Arg.Any<CancellationToken>());
        Assert.Empty(flight.DomainEvents);
    }

    [Fact]
    public async Task KnownStatus_ANewEstimatedDeparture_IsPlannedFromTheNewTime()
    {
        // Live before the call; the estimate moves departure inside the 3 h Fast window, with no new status.
        var flight = GivenKnownStatus(Status() with { EstimatedDepartureUtc = Now.AddHours(2) }, departureUtc: Now.AddHours(10));

        await Sync();

        Assert.Equal(FlightSyncMode.Fast, flight.SyncMode);
        Assert.Equal(Now.AddMinutes(3), flight.NextSyncDueUtc);
    }
```

- [ ] **Step 2: Run the tests and watch them fail.**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests"`

Expected:
- **Fail:**
  - `KnownStatus_ANewGate_RecordsADetailChange_WithNoPush`
  - `KnownStatus_ABaggageClaimReportedLater_…`
  - `KnownStatus_ANewEstimatedArrival_…`
  - `KnownStatus_ANewEstimatedDeparture_…`
- **Pass already, because they guard current behaviour** (record them as "passes before, guard"):
  - `NewStatus_StillRaisesFlightStatusChanged`
  - `KnownStatus_ADetailNoLongerReported_RecordsNothing`
  - `KnownStatus_AnIdenticalResponse_…`
- **Existing tests.** Every existing test still passes, including `DuplicateStatus_ReturnsFalse_AddsNoEvent_ButRecordsTheSync`: its latest event is unstubbed (null), so it records nothing.

- [ ] **Step 3: Write the rule** in `SyncFlightStatusCommand.cs`.

(a) Replace

```csharp
            var dedupHash = FlightStatusEvent.ComputeDedupHash(flight.Id, statusId.Value, result.EffectiveAtUtc, _provider.ProviderName, result.ExternalId);
            if (await _flights.FlightStatusEventExistsAsync(dedupHash, ct)) return false;
```

with

```csharp
            var dedupHash = FlightStatusEvent.ComputeDedupHash(flight.Id, statusId.Value, result.EffectiveAtUtc, _provider.ProviderName, result.ExternalId);
            if (await _flights.FlightStatusEventExistsAsync(dedupHash, ct))
                return await ApplyKnownStatusAsync(flight, statusId.Value, result, now, ct);
```

(b) Replace the summary above `ApplyLiveStatusAsync`:

```csharp
        /// <summary>
        /// Fetches the live status and, if it's new, records it. Changes the flight only after every
        /// step that can throw, so a failure leaves nothing half-applied for the deferral to save.
        /// </summary>
```

with

```csharp
        /// <summary>
        /// Fetches the live status and records it. A new status becomes an event and raises the push; a
        /// status the flight already has goes to <see cref="ApplyKnownStatusAsync"/> (#351). Changes the
        /// flight only after every step that can throw, so a failure leaves nothing half-applied for the
        /// deferral to save.
        /// </summary>
```

(c) Add these two methods directly after `ApplyLiveStatusAsync`:

```csharp

        /// <summary>
        /// The provider reported a status this flight already has (#351). The response used to be dropped.
        /// Now its times update the flight, and a newly reported gate, terminal or baggage claim is recorded
        /// as a detail-change event. Neither raises <c>FlightStatusChanged</c>, so neither sends a push. A
        /// detail the provider no longer reports keeps its last known value. Returns whether an event was
        /// recorded. The flight changes only after the latest-event read, the step that can throw.
        /// </summary>
        private async Task<bool> ApplyKnownStatusAsync(
            Flight flight, int statusId, FlightStatusResult result, DateTime now, CancellationToken ct)
        {
            var latest = await _flights.GetLatestFlightStatusEventAsync(flight.Id, ct);

            flight.ApplyProviderTimes(result.EstimatedDepartureUtc, result.EstimatedArrivalUtc,
                result.ActualDepartureUtc, result.ActualArrivalUtc, result.EquipmentIcao, now);

            if (latest is null || !ReportsNewDetails(result, latest))
                return false;

            await _flights.AddFlightStatusEventAsync(FlightStatusEvent.CreateDetailChange(
                flight.Id, statusId, result.EffectiveAtUtc, _provider.ProviderName, result.ExternalId, latest.Id,
                gate: result.Gate ?? latest.Gate,
                terminal: result.Terminal ?? latest.Terminal,
                baggageClaim: result.BaggageClaim ?? latest.BaggageClaim,
                rawPayloadJson: result.RawPayloadJson), ct);
            return true;
        }

        /// <summary>A detail is new only when the provider reports a value that differs from the latest event's.</summary>
        private static bool ReportsNewDetails(FlightStatusResult result, FlightStatusEvent latest) =>
            (result.Gate is not null && result.Gate != latest.Gate)
            || (result.Terminal is not null && result.Terminal != latest.Terminal)
            || (result.BaggageClaim is not null && result.BaggageClaim != latest.BaggageClaim);
```

- [ ] **Step 4: Run the tests and watch them pass.**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests"`
Expected: all PASS.

- [ ] **Step 5: Prove the tests.** Make each break, run the named test, see it fail, then restore with `mv … .bak` and `touch`.
1. Put back `if (await _flights.FlightStatusEventExistsAsync(dedupHash, ct)) return false;`. `KnownStatus_ANewGate_…`, `KnownStatus_ABaggageClaimReportedLater_…`, `KnownStatus_ANewEstimatedArrival_…` and `KnownStatus_ANewEstimatedDeparture_…` fail.
2. Replace `result.Gate ?? latest.Gate` with `result.Gate`. `KnownStatus_ABaggageClaimReportedLater_IsRecorded_KeepingDetailsNoLongerReported` fails.
3. In `ReportsNewDetails`, change `(result.Gate is not null && result.Gate != latest.Gate)` to `result.Gate != latest.Gate`. `KnownStatus_ADetailNoLongerReported_RecordsNothing` fails.
4. Replace the `flight.ApplyProviderTimes(...)` call with `flight.ApplyStatusUpdate("DEP", result.EstimatedDepartureUtc, result.EstimatedArrivalUtc, result.ActualDepartureUtc, result.ActualArrivalUtc, result.EquipmentIcao, now);`. `KnownStatus_ANewEstimatedArrival_UpdatesTheFlight_WithNoEventOrPush` fails on `DomainEvents`.
5. Replace `FlightStatusEvent.CreateDetailChange(` with a `FlightStatusEvent.Create(flight.Id, statusId, result.EffectiveAtUtc, _provider.ProviderName, result.ExternalId, result.Gate ?? latest.Gate, result.Terminal ?? latest.Terminal, result.BaggageClaim ?? latest.BaggageClaim, rawPayloadJson: result.RawPayloadJson)` call. `KnownStatus_ANewGate_RecordsADetailChange_WithNoPush` fails on `DedupHash`.

- [ ] **Step 6: Document it in `CLAUDE.md`.** Directly after the paragraph that starts `**An unrecognised status code degrades, it does not abort.**`, add:

```markdown
**A status the flight already has still updates it (#351).** `SyncFlightStatusCommandHandler` records a new status as a `FlightStatusEvent` and calls `Flight.ApplyStatusUpdate`, which raises `FlightStatusChanged` and so the push. When the status hash (`FlightStatusEvent.ComputeDedupHash`: flight, status, effective time, source, provider id) is already stored, the response is no longer dropped. `Flight.ApplyProviderTimes` takes its estimates, actuals and aircraft type without raising an event. A gate, terminal or baggage claim that differs from the flight's latest event (the highest id) is recorded with `FlightStatusEvent.CreateDetailChange`. Neither sends a push. A detail the provider stops reporting keeps its last known value. A detail change can't reuse the status hash, because `(FlightId, Source, DedupHash)` is unique, so `ComputeDetailChangeHash` hashes the status identity plus the id of the event it follows and the details, which stays unique even when a gate changes back. **Never change `ComputeDedupHash`'s formula:** every stored hash would stop matching, and each tracked flight would record, and push, its latest status again (`ComputeDedupHash_FormulaIsUnchanged` pins it). Event lists order by effective time, then id, so a detail change sorts above the status event whose effective time it shares.
```

- [ ] **Step 7: Build and run everything.**
- Run `dotnet build --configuration Release`. Expected: 0 errors, 0 warnings.
- Run the full `dotnet test` once (Docker is running) and record the per-project counts.
- Confirm `spec/openapi.json` is unchanged: run `FlightSync__Enabled=false scripts/generate-openapi.sh`, then `git diff --stat spec/openapi.json` should print nothing.

- [ ] **Step 8: Commit.**

```bash
git add FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs \
  CLAUDE.md \
  FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs
git commit -m "fix: a sync that reports a status the flight already has keeps its new times and gate, terminal and baggage claim (#351)"
```

(End the message with a blank line and your attribution trailer.)
