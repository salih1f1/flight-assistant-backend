# Tracked Flight Cap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A traveler can track at most `FlightSync:MaxActiveTrackedFlightsPerTraveler` (default 20) flights that haven't finished. Tracking another returns 409 and writes nothing. Tracking a flight the traveler already tracks still succeeds (#380).

**Architecture:**
- **The count (Infrastructure).** `IFlightRepository.CountActiveTrackedFlightsAsync(travelerId, nowUtc)` counts, in SQL, the traveler's tracked flights that have no actual arrival, aren't cancelled, and have an estimated (else scheduled) arrival after `nowUtc`.
- **The setting (API options, read through Application).** `MaxActiveTrackedFlightsPerTraveler` lives on `FlightSyncOptions` / `IFlightSyncOptions`, next to `ManualSyncCooldown`, the other traveler-facing cost limit.
- **The rule (Application).** `TrackFlightCommandHandler` checks the count before creating the flight row. At the cap, a flight the traveler already tracks still returns its id; any other flight throws a 409 `DomainException`. Both `POST /flights` and `POST /admin/travelers/{id}/flights` send this command.

**Tech Stack:** .NET 10, EF Core + Npgsql, MediatR, xUnit, NSubstitute, Testcontainers PostgreSQL (#295), `Microsoft.Extensions.TimeProvider.Testing`.

**Spec:** `docs/superpowers/plans/2026-09-15-flight-data-cost-reduction.md` (Phase 1, item 7) and issue #380.

## Global Constraints

- Work only in the worktree `/Users/salihalishov/Repositories/flight-assistant-backend-380`, branch `feature/380-tracked-flight-cap`. Never touch the main checkout or other worktrees.
- **Active tracked flight.** A `traveler_flights` row for the traveler whose `Flight` has `ActualArrivalUtc == null`, `CancelledAtUtc == null`, and `(EstimatedArrivalUtc ?? ScheduledArrivalUtc) > nowUtc`. It is counted in SQL, never by loading the traveler's flights.
- **The repository API.** `Task<int> CountActiveTrackedFlightsAsync(Guid travelerId, DateTime nowUtc, CancellationToken ct = default)` on `IFlightRepository`.
- **The setting.** `FlightSync:MaxActiveTrackedFlightsPerTraveler`:
  - an `int`, default `20`, validated with `[Range(1, 1000)]`;
  - on `FlightSyncOptions` (API) and `IFlightSyncOptions` (Application);
  - listed in `FlightAssistant.API/appsettings.json`'s `FlightSync` section.
- **The rule.** `TrackFlightCommandHandler` checks the cap after the airline and airport lookups and **before** `GetOrCreateFlightAsync`.
  - When `count >= MaxActiveTrackedFlightsPerTraveler` and the traveler doesn't already track this flight, it throws `DomainException("You can track up to {N} flights at a time. Stop tracking one to add another.", 409)`. `{N}` is the configured cap.
  - A flight the traveler already tracks (found by airline id, normalized flight number and service date, then the tracking row) succeeds at the cap without adding a row.
- **API and schema.**
  - No DTO, route, schema or migration change.
  - Both track endpoints gain `ProducesProblem(409)` and a sentence in their descriptions, so `spec/openapi.json` is regenerated with `FlightSync__Enabled=false scripts/generate-openapi.sh` and committed in Task 2.
- **No paid calls.** Make no call to AeroAPI or any other paid external API. Never run the API with sync on. Never run `dotnet ef database update`.
- **Test values.** Tests with option values use **non-default** values (#308); the handler tests' cap is 3.
- **SQL tests.** A LINQ predicate that must translate to SQL is proved against PostgreSQL through the Testcontainers harness, not only InMemory (#295).
- **The build stays warning-free** (#301).
- **TDD.** Every new test is seen failing first. Each task ends with a prove step: break the code on purpose, watch the named test fail, then restore. After restoring from a `.bak`, `touch` the file so MSBuild recompiles.
- **Commits.** Commit with explicit paths only (never `-A` or `.`). Every commit message ends with a blank line and the attribution trailer your own instructions require. Never push.

## Decisions made while planning

These are recorded so review doesn't relitigate them.

- **The handler, not the validator.** The plan named `TrackFlightCommandValidator`. A validator can't tell that the traveler already tracks this flight without repeating the airline lookup and the flight lookup, and rejecting that request would break the endpoint's documented idempotency ("tracking a flight the traveler already tracks … still returns 201").
- **409 Conflict, not 400.** The request is well-formed; it conflicts with the traveler's current tracking list. The ProblemDetails title carries the message.
- **"Active" comes from the flight's times and status, not its stored `SyncMode`.**
  - A stale or never-advanced mode can't hold a slot. That happens on a local run with `FlightSync:Enabled=false`, or with a flight nobody has synced since it landed.
  - It matches the track validator's "already landed" rule (#350).
  - A cancelled flight frees its slot straight away.
- **The admin route gets the same cap.** It sends the same command, and the cap protects spend no matter who adds the flight.
- **Accepted:**
  - Travelers already above the cap aren't trimmed; they can't add another flight until they're below it.
  - Two concurrent tracks at cap − 1 can both succeed, since the count isn't locked. The overshoot is bounded by concurrent requests from one account.

## Commands

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-380
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FlightRepositoryTests"
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightSyncOptionsTests"
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~TrackFlightCommandHandlerTests"
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~AdminTravelerEndpointTests"
dotnet build --configuration Release
FlightSync__Enabled=false scripts/generate-openapi.sh
```

---

### Task 1: The cap setting and the active-flight count

**Files:**
- Modify: `FlightAssistant.Application/FlightSync/IFlightSyncOptions.cs`
- Modify: `FlightAssistant.API/HostedServices/FlightSyncOptions.cs`
- Modify: `FlightAssistant.API/appsettings.json`
- Modify: `FlightAssistant.Application/Interfaces/Repositories/IFlightRepository.cs`
- Modify: `FlightAssistant.Infrastructure/Persistance/Repositories/FlightRepository.cs`
- Modify: `CLAUDE.md` (Required Configuration row)
- Test: `FlightAssistant.Infrastructure.Tests/Persistance/Repositories/FlightRepositoryTests.cs`
- Test: `FlightAssistant.API.Tests/HostedServices/FlightSyncOptionsTests.cs`
- Test (fakes only): `FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs`, `FlightAssistant.Application.Tests/Commands/Flights/TrackFlightCommandHandlerTests.cs`, `FlightAssistant.Application.Tests/FlightSync/FlightSyncPlannerTests.cs`

**Interfaces:**
- Produces:
  - `IFlightSyncOptions.MaxActiveTrackedFlightsPerTraveler` (`int`).
  - `IFlightRepository.CountActiveTrackedFlightsAsync(Guid travelerId, DateTime nowUtc, CancellationToken ct = default)` returning `Task<int>`.
  - The `TrackFlightCommandHandlerTests.SyncOptions` fake returns `3` for the new member.

Task 2 consumes all three.

- [ ] **Step 1: Write the failing repository tests.** In `FlightRepositoryTests.cs`, add this helper directly after `AddTracker`:

```csharp
    /// <summary>A saved flight scheduled to arrive at <paramref name="scheduledArrivalUtc"/>, tracked by
    /// <paramref name="travelerId"/> when one is given.</summary>
    private static Flight FlightArriving(FlightAssistantDbContext db, string flightNumber, DateTime scheduledArrivalUtc,
        Guid? travelerId = null)
    {
        var (airlineId, departureAirportId, arrivalAirportId) = EnsureReferenceData(db);
        var flight = Flight.Create(airlineId, flightNumber, departureAirportId, arrivalAirportId,
            DateOnly.FromDateTime(scheduledArrivalUtc), scheduledArrivalUtc.AddHours(-3), scheduledArrivalUtc);
        db.Flights.Add(flight);
        db.SaveChanges();

        if (travelerId is { } id)
        {
            EnsureTraveler(db, id);
            db.TravelerFlights.Add(TravelerFlight.Create(id, flight.Id));
            db.SaveChanges();
        }

        return flight;
    }
```

Then add these tests at the end of the class:

```csharp
    // ── CountActiveTrackedFlightsAsync (#380) ──

    [Fact]
    public async Task CountActiveTrackedFlightsAsync_CountsOnlyTheTravelersFlightsThatHaveNotFinished()
    {
        using var db = CreateContext();
        var traveler = Guid.NewGuid();

        // Counts: arrives in 3 hours.
        FlightArriving(db, "TK10", SyncNow.AddHours(3), traveler);
        // Counts: its scheduled arrival has passed, but the estimate is still ahead.
        var delayed = FlightArriving(db, "TK11", SyncNow.AddHours(-1), traveler);
        delayed.ApplyStatusUpdate("DEP", null, SyncNow.AddHours(1), SyncNow.AddHours(-3), null, null, SyncNow);
        // Doesn't count: it has an actual arrival, although its scheduled arrival is ahead.
        var landed = FlightArriving(db, "TK12", SyncNow.AddHours(3), traveler);
        landed.ApplyStatusUpdate("ARR", null, null, SyncNow.AddHours(-2), SyncNow.AddMinutes(-5), null, SyncNow);
        // Doesn't count: cancelled, although its scheduled arrival is ahead.
        var cancelled = FlightArriving(db, "TK13", SyncNow.AddHours(3), traveler);
        cancelled.ApplyStatusUpdate("CAN", null, null, null, null, null, SyncNow);
        // Doesn't count: its scheduled arrival has passed and there's no estimate.
        FlightArriving(db, "TK14", SyncNow.AddHours(-1), traveler);
        // Doesn't count: another traveler's flight, and an untracked one.
        FlightArriving(db, "TK15", SyncNow.AddHours(3), Guid.NewGuid());
        FlightArriving(db, "TK16", SyncNow.AddHours(3));
        db.SaveChanges();

        var count = await Repository(db, ClockAt(SyncNow)).CountActiveTrackedFlightsAsync(traveler, SyncNow);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountActiveTrackedFlightsAsync_AFlightArrivingExactlyNow_HasFinished()
    {
        using var db = CreateContext();
        var traveler = Guid.NewGuid();
        FlightArriving(db, "TK20", SyncNow, traveler);
        FlightArriving(db, "TK21", SyncNow.AddMinutes(1), traveler);

        var count = await Repository(db, ClockAt(SyncNow)).CountActiveTrackedFlightsAsync(traveler, SyncNow);

        Assert.Equal(1, count);
    }
```

- [ ] **Step 2: Write the failing options tests** in `FlightSyncOptionsTests.cs`.
- In `Defaults_MatchTheSpec`, below `Assert.Equal(TimeSpan.FromMinutes(5), options.ManualSyncCooldown);`, add:

```csharp
        Assert.Equal(20, options.MaxActiveTrackedFlightsPerTraveler);
```

- In `IFlightSyncOptions_ReflectsTheBoundSection`, add `["FlightSync:MaxActiveTrackedFlightsPerTraveler"] = "7",` below the `ManualSyncCooldown` setting. Below `Assert.Equal(TimeSpan.FromMinutes(2), options.ManualSyncCooldown);`, add:

```csharp
        Assert.Equal(7, options.MaxActiveTrackedFlightsPerTraveler);
```

- In `AppSettingsJson_FlightSyncSection_MatchesTheCodeDefaults`, below the `ManualSyncCooldown` assertion, add:

```csharp
        Assert.Equal(defaults.MaxActiveTrackedFlightsPerTraveler, shipped.MaxActiveTrackedFlightsPerTraveler);
```

- In `AnOutOfRangeValue_FailsOnStart`, add two cases below the last `[InlineData]`:

```csharp
    [InlineData("MaxActiveTrackedFlightsPerTraveler", "0")]
    [InlineData("MaxActiveTrackedFlightsPerTraveler", "1001")]
```

- [ ] **Step 3: Give the `IFlightSyncOptions` fakes the new member.** In each `private sealed class SyncOptions : IFlightSyncOptions`, in `SyncFlightStatusCommandHandlerTests.cs`, `TrackFlightCommandHandlerTests.cs` and `FlightSyncPlannerTests.cs`, add below the `ManualSyncCooldown` member:

```csharp
        public int MaxActiveTrackedFlightsPerTraveler => 3;
```

- [ ] **Step 4: Run the tests and watch them fail.**

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FlightRepositoryTests"`
Expected: build FAILS, because `CountActiveTrackedFlightsAsync` and `MaxActiveTrackedFlightsPerTraveler` don't exist.

- [ ] **Step 5: Add the setting.**

(a) In `FlightAssistant.Application/FlightSync/IFlightSyncOptions.cs`, below `ManualSyncCooldown`, add:

```csharp

        /// <summary>How many flights that haven't finished a traveler can track at once (#380).
        /// Tracking another is refused with a 409.</summary>
        int MaxActiveTrackedFlightsPerTraveler { get; }
```

(b) In `FlightAssistant.API/HostedServices/FlightSyncOptions.cs`, below `ManualSyncCooldown`, add:

```csharp

        /// <summary>How many flights that haven't finished a traveler can track at once (#380). Each
        /// becomes a Live and then Fast poll, so this bounds one account's AeroAPI spend.</summary>
        [Range(1, 1000)]
        public int MaxActiveTrackedFlightsPerTraveler { get; set; } = 20;
```

(c) In `FlightAssistant.API/appsettings.json`, in the top-level `"FlightSync"` section (the one with `"ManualSyncCooldown"`, not `RateLimiting:FlightSync`), replace

```json
    "ManualSyncCooldown": "00:05:00"
```

with

```json
    "ManualSyncCooldown": "00:05:00",
    "MaxActiveTrackedFlightsPerTraveler": 20
```

- [ ] **Step 6: Add the count.**

(a) In `IFlightRepository.cs`, below `IsFlightTrackedAsync`, add:

```csharp

        /// <summary>
        /// How many flights the traveler tracks that haven't finished as of <paramref name="nowUtc"/>
        /// (#380): no actual arrival, not cancelled, and an estimated (else scheduled) arrival still
        /// ahead. Counted in SQL, since <c>traveler_flights</c> keeps every flight a traveler has ever
        /// tracked.
        /// </summary>
        Task<int> CountActiveTrackedFlightsAsync(Guid travelerId, DateTime nowUtc, CancellationToken ct = default);
```

(b) In `FlightRepository.cs`, directly after `IsFlightTrackedAsync`, add:

```csharp

        public Task<int> CountActiveTrackedFlightsAsync(Guid travelerId, DateTime nowUtc, CancellationToken ct = default) =>
            _db.TravelerFlights
                .Where(tf => tf.TravelerId == travelerId)
                .Join(_db.Flights, tf => tf.FlightId, f => f.Id, (tf, f) => f)
                .CountAsync(f => f.ActualArrivalUtc == null
                                 && f.CancelledAtUtc == null
                                 && (f.EstimatedArrivalUtc ?? f.ScheduledArrivalUtc) > nowUtc, ct);
```

- [ ] **Step 7: Run the tests and watch them pass.**

Run:
- `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FlightRepositoryTests"`
- `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightSyncOptionsTests"`
- `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests|FullyQualifiedName~TrackFlightCommandHandlerTests|FullyQualifiedName~FlightSyncPlannerTests"`

Expected: all PASS.

- [ ] **Step 8: Prove the tests.** Make each break, run the named test, see it fail, then restore with `mv <file>.bak <file> && touch <file>`.
1. Delete `&& f.CancelledAtUtc == null`. `CountActiveTrackedFlightsAsync_CountsOnlyTheTravelersFlightsThatHaveNotFinished` fails with 3.
2. Replace `(f.EstimatedArrivalUtc ?? f.ScheduledArrivalUtc)` with `f.ScheduledArrivalUtc`. The same test fails with 1.
3. Delete `f.ActualArrivalUtc == null &&`. The same test fails with 3.
4. Change `> nowUtc` to `>= nowUtc`. `CountActiveTrackedFlightsAsync_AFlightArrivingExactlyNow_HasFinished` fails with 2.
5. Change the default to `25`. `Defaults_MatchTheSpec` and `AppSettingsJson_FlightSyncSection_MatchesTheCodeDefaults` fail.
6. Delete `[Range(1, 1000)]`. Both new `AnOutOfRangeValue_FailsOnStart` cases fail.

- [ ] **Step 9: Document the setting in `CLAUDE.md`.** In the Required Configuration table, replace the row

```markdown
| `FlightSync:LoopInterval`, `NearDepartureInterval`, `Interval`, `ScheduleCheckInterval`, `FastWindowBeforeDeparture`, `StopAfterArrival`, `StopWithoutArrivalAfter`, `ManualSyncCooldown` | Optional; defaults 1 min, 5 min (Fast), 30 min (Live), 1 day, 4 h, 30 min, 1 day, 5 min (traveler manual-sync cooldown, #369); Fast and Live lengthened for cost in #364. `ScheduleCheckInterval` has no effect until step 2 of #349 (the `/schedules` check). Validated on start |
```

with

```markdown
| `FlightSync:LoopInterval`, `NearDepartureInterval`, `Interval`, `ScheduleCheckInterval`, `FastWindowBeforeDeparture`, `StopAfterArrival`, `StopWithoutArrivalAfter`, `ManualSyncCooldown`, `MaxActiveTrackedFlightsPerTraveler` | Optional; defaults 1 min, 5 min (Fast), 30 min (Live), 1 day, 4 h, 30 min, 1 day, 5 min (traveler manual-sync cooldown, #369), 20 (flights a traveler can track that haven't finished, 1–1000, #380); Fast and Live lengthened for cost in #364. `ScheduleCheckInterval` has no effect until step 2 of #349 (the `/schedules` check). Validated on start |
```

- [ ] **Step 10: Build.** Run `dotnet build --configuration Release`. Expected: 0 errors, 0 warnings.

- [ ] **Step 11: Commit.**

```bash
git add FlightAssistant.Application/FlightSync/IFlightSyncOptions.cs \
  FlightAssistant.API/HostedServices/FlightSyncOptions.cs \
  FlightAssistant.API/appsettings.json \
  FlightAssistant.Application/Interfaces/Repositories/IFlightRepository.cs \
  FlightAssistant.Infrastructure/Persistance/Repositories/FlightRepository.cs \
  CLAUDE.md \
  FlightAssistant.Infrastructure.Tests/Persistance/Repositories/FlightRepositoryTests.cs \
  FlightAssistant.API.Tests/HostedServices/FlightSyncOptionsTests.cs \
  FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs \
  FlightAssistant.Application.Tests/Commands/Flights/TrackFlightCommandHandlerTests.cs \
  FlightAssistant.Application.Tests/FlightSync/FlightSyncPlannerTests.cs
git commit -m "feat: count a traveler's tracked flights that haven't finished, and a setting to cap them (#380)"
```

(End the message with a blank line and your attribution trailer.)

---

### Task 2: Tracking past the cap returns 409

**Files:**
- Modify: `FlightAssistant.Application/Commands/Flights/TrackFlightCommand.cs`
- Modify: `FlightAssistant.API/Endpoints/FlightEndpoints.cs` (the `TrackFlight` endpoint)
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminTravelerEndpoints.cs` (the `AdminTrackFlightForTraveler` endpoint)
- Modify: `spec/openapi.json` (regenerated)
- Modify: `CLAUDE.md`
- Test: `FlightAssistant.Application.Tests/Commands/Flights/TrackFlightCommandHandlerTests.cs`
- Test: `FlightAssistant.API.Tests/Integration/AdminTravelerEndpointTests.cs`

**Interfaces:**
- Consumes: `IFlightSyncOptions.MaxActiveTrackedFlightsPerTraveler`, `IFlightRepository.CountActiveTrackedFlightsAsync(Guid, DateTime, CancellationToken)`, and the fake's cap of 3, all from Task 1. It also uses the existing `IFlightRepository.GetByFlightNumberAndDateAsync(int airlineId, string flightNumber, DateOnly serviceDate, CancellationToken)` and `GetTravelerFlightAsync(Guid, int, CancellationToken)`.
- Produces: none (the last task).

- [ ] **Step 1: Write the failing handler tests.** At the end of `TrackFlightCommandHandlerTests`, add:

```csharp
    // ── Cap on tracked flights that haven't finished (#380); the fake's cap is 3 ──

    private static Flight ExistingFlight() => Flight.Create(
        1, "BA123", 10, 20, DefaultCmd.ServiceDate, DefaultCmd.ScheduledDepartureUtc, DefaultCmd.ScheduledArrivalUtc);

    [Fact]
    public async Task AtTheCap_ANewFlight_Throws409_AndWritesNothing()
    {
        _flights.CountActiveTrackedFlightsAsync(DefaultCmd.TravelerId, Now, Arg.Any<CancellationToken>()).Returns(3);
        _flights.GetByFlightNumberAndDateAsync(1, "BA123", DefaultCmd.ServiceDate, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() => _handler.Handle(DefaultCmd, CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("You can track up to 3 flights at a time. Stop tracking one to add another.", ex.Message);
        await _flights.DidNotReceive().GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().AddTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OneBelowTheCap_TracksTheFlight()
    {
        _flights.CountActiveTrackedFlightsAsync(DefaultCmd.TravelerId, Now, Arg.Any<CancellationToken>()).Returns(2);
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>()).Returns(ci => ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await _handler.Handle(DefaultCmd, CancellationToken.None);

        await _flights.Received(1).AddTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AtTheCap_AFlightTheTravelerAlreadyTracks_StillReturnsItsId_WithoutAddingTracking()
    {
        var existing = ExistingFlight();
        _flights.CountActiveTrackedFlightsAsync(DefaultCmd.TravelerId, Now, Arg.Any<CancellationToken>()).Returns(3);
        _flights.GetByFlightNumberAndDateAsync(1, "BA123", DefaultCmd.ServiceDate, Arg.Any<CancellationToken>()).Returns(existing);
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>()).Returns(existing);
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, existing.Id, Arg.Any<CancellationToken>())
            .Returns(TravelerFlight.Create(DefaultCmd.TravelerId, existing.Id));

        var flightId = await _handler.Handle(DefaultCmd, CancellationToken.None);

        Assert.Equal(existing.Id, flightId);
        await _flights.DidNotReceive().AddTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AtTheCap_AnExistingFlightTheTravelerDoesNotTrack_Throws409()
    {
        var existing = ExistingFlight();
        _flights.CountActiveTrackedFlightsAsync(DefaultCmd.TravelerId, Now, Arg.Any<CancellationToken>()).Returns(3);
        _flights.GetByFlightNumberAndDateAsync(1, "BA123", DefaultCmd.ServiceDate, Arg.Any<CancellationToken>()).Returns(existing);
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, existing.Id, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() => _handler.Handle(DefaultCmd, CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        await _flights.DidNotReceive().AddTravelerFlightAsync(Arg.Any<TravelerFlight>(), Arg.Any<CancellationToken>());
    }
```

- [ ] **Step 2: Write the failing API test.** In `AdminTravelerEndpointTests.cs`:
- Add `using FlightAssistant.Application.FlightSync;`.
- Directly after `Track_Admin_SameFlightTwice_Returns201BothTimes_AndTracksItOnce`, add:

```csharp
    [Fact]
    public async Task Track_Admin_AtTheCap_Returns409_AndTracksNothingMore()
    {
        // The real host: the configured cap reaches the handler, and the count runs against the database (#380).
        var cap = factory.Services.GetRequiredService<IFlightSyncOptions>().MaxActiveTrackedFlightsPerTraveler;
        var traveler = await SeedTravelerAsync();
        var client = factory.CreateAdminClient();
        for (var i = 0; i < cap; i++)
        {
            var tracked = await client.PostAsJsonAsync($"admin/travelers/{traveler.Id}/flights", await SeedTrackableFlightAsync());
            Assert.Equal(HttpStatusCode.Created, tracked.StatusCode);
        }

        var refused = await client.PostAsJsonAsync($"admin/travelers/{traveler.Id}/flights", await SeedTrackableFlightAsync());

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(cap, (await TrackingRowsForAsync(traveler.Id)).Count);
    }
```

- [ ] **Step 3: Run the tests and watch them fail.**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~TrackFlightCommandHandlerTests"`
Expected: `AtTheCap_ANewFlight_Throws409_AndWritesNothing`, `AtTheCap_AnExistingFlightTheTravelerDoesNotTrack_Throws409` FAIL (no exception is thrown). The other two new tests pass already.

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~Track_Admin_AtTheCap_Returns409_AndTracksNothingMore"`
Expected: FAIL, because the request past the cap returns 201.

- [ ] **Step 4: Write the rule** in `TrackFlightCommand.cs`.

(a) Replace

```csharp
            // Look up or create the Flight aggregate (race-safe via the unique ix_flight_lookup index)
            var flightNumber = cmd.FlightNumber.Trim().ToUpperInvariant();
```

with

```csharp
            var flightNumber = cmd.FlightNumber.Trim().ToUpperInvariant();
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // A cap on flights the traveler tracks that haven't finished (#380), since each becomes a Live
            // and then Fast poll. Checked before the flight row exists, so a refused request writes nothing.
            // A flight the traveler already tracks still succeeds, so tracking stays idempotent.
            var cap = _syncOptions.MaxActiveTrackedFlightsPerTraveler;
            if (await _flights.CountActiveTrackedFlightsAsync(cmd.TravelerId, now, ct) >= cap
                && !await AlreadyTracksAsync(cmd.TravelerId, airlineId, flightNumber, cmd.ServiceDate, ct))
            {
                throw new DomainException(
                    $"You can track up to {cap} flights at a time. Stop tracking one to add another.", 409);
            }

            // Look up or create the Flight aggregate (race-safe via the unique ix_flight_lookup index)
```

(b) Inside `if (flight.SyncMode is null)`, delete the now-duplicate line

```csharp
                    var now = _timeProvider.GetUtcNow().UtcDateTime;
```

so the planning call uses the `now` declared above.

(c) Add this method to `TrackFlightCommandHandler`, after `Handle`:

```csharp

        /// <summary>Whether the traveler already tracks this flight, found the way
        /// <c>GetOrCreateFlightAsync</c> would find its row.</summary>
        private async Task<bool> AlreadyTracksAsync(
            Guid travelerId, int airlineId, string flightNumber, DateOnly serviceDate, CancellationToken ct)
        {
            var flight = await _flights.GetByFlightNumberAndDateAsync(airlineId, flightNumber, serviceDate, ct);
            return flight is not null && await _flights.GetTravelerFlightAsync(travelerId, flight.Id, ct) is not null;
        }
```

- [ ] **Step 5: Run the tests and watch them pass.**

Run:
- `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~TrackFlightCommandHandlerTests"`
- `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~AdminTravelerEndpointTests|FullyQualifiedName~FlightEndpointTests"`

Expected: all PASS. Every existing tracking test passes unchanged, because an unconfigured count returns 0.

- [ ] **Step 6: Prove the tests.** Make each break, run the named test, see it fail, then restore with `mv … .bak` and `touch`.
1. Change `>= cap` to `> cap`. `AtTheCap_ANewFlight_Throws409_AndWritesNothing` fails.
2. Delete `&& !await AlreadyTracksAsync(...)`. `AtTheCap_AFlightTheTravelerAlreadyTracks_StillReturnsItsId_WithoutAddingTracking` fails with a 409.
3. In `AlreadyTracksAsync`, return `flight is not null`, skipping the tracking-row check. `AtTheCap_AnExistingFlightTheTravelerDoesNotTrack_Throws409` fails.
4. Move the whole cap `if` block below `var flight = await _flights.GetOrCreateFlightAsync(candidate, ct);`. `AtTheCap_ANewFlight_Throws409_AndWritesNothing` fails on `DidNotReceive().GetOrCreateFlightAsync`.
5. Delete the whole cap `if` block. `Track_Admin_AtTheCap_Returns409_AndTracksNothingMore` fails with 201.

- [ ] **Step 7: Update the endpoint descriptions.**

(a) In `FlightAssistant.API/Endpoints/FlightEndpoints.cs`, in the `TrackFlight` description, replace

```csharp
                "DepartureAirportIata, ArrivalAirportIata, ScheduledDepartureUtc, ScheduledArrivalUtc.")
            .Produces(201)
            .ProducesValidationProblem();
```

with

```csharp
                "DepartureAirportIata, ArrivalAirportIata, ScheduledDepartureUtc, ScheduledArrivalUtc. " +
                "A traveler can track up to 20 flights at a time by default; a flight stops counting once it lands, is cancelled, or its arrival time passes. " +
                "Tracking another returns 409 and adds nothing; tracking a flight the traveler already tracks still returns 201.")
            .Produces(201)
            .ProducesValidationProblem()
            .ProducesProblem(409);
```

(b) In `FlightAssistant.API/Endpoints/Admin/AdminTravelerEndpoints.cs`, in the `AdminTrackFlightForTraveler` description, replace

```csharp
                "its flightId. An unknown traveler is a 404 and writes nothing.")
            .Produces(201)
            .ProducesValidationProblem()
            .ProducesProblem(404);
```

with

```csharp
                "its flightId. An unknown traveler is a 404 and writes nothing. " +
                "The same cap as POST /flights applies: at the traveler's limit of flights tracked at a time, tracking another returns 409 and writes nothing.")
            .Produces(201)
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(409);
```

(c) Run `FlightSync__Enabled=false scripts/generate-openapi.sh` from the worktree root. Then run `git diff --stat spec/openapi.json`. Expected: only the two track operations change, each with its description and a 409 response.

- [ ] **Step 8: Document it in `CLAUDE.md`.** At the end of the paragraph that starts `**Each tracked flight is synced on its own schedule (#356).**` (after its last sentence, "…multiplies the effective limit by the instance count."), append on the same line:

```markdown
 A traveler can track at most `FlightSync:MaxActiveTrackedFlightsPerTraveler` (default 20) flights that haven't finished — no `ActualArrivalUtc`, no `CancelledAtUtc`, and an estimated (else scheduled) arrival still ahead — counted in SQL by `IFlightRepository.CountActiveTrackedFlightsAsync` (#380). The count uses times and status rather than the stored `SyncMode`, so a stale mode can't hold a slot. `TrackFlightCommandHandler` checks it before creating the flight row, so a refused track (409, on both `POST /flights` and the admin route) writes nothing, and a flight the traveler already tracks still returns 201 at the cap. Travelers already above the cap keep their flights.
```

- [ ] **Step 9: Build and run everything.**
- Run `dotnet build --configuration Release`. Expected: 0 errors, 0 warnings.
- Run the full `dotnet test` once (Docker is running) and record the per-project counts.

- [ ] **Step 10: Commit.**

```bash
git add FlightAssistant.Application/Commands/Flights/TrackFlightCommand.cs \
  FlightAssistant.API/Endpoints/FlightEndpoints.cs \
  FlightAssistant.API/Endpoints/Admin/AdminTravelerEndpoints.cs \
  spec/openapi.json \
  CLAUDE.md \
  FlightAssistant.Application.Tests/Commands/Flights/TrackFlightCommandHandlerTests.cs \
  FlightAssistant.API.Tests/Integration/AdminTravelerEndpointTests.cs
git commit -m "feat: tracking a flight past the traveler's cap returns 409 and writes nothing (#380)"
```

(End the message with a blank line and your attribution trailer.)
