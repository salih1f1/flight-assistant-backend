# Manual Sync Cooldown and Rate Limit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A traveler's manual sync makes at most one billed AeroAPI call per flight per cooldown, and one traveler can't flood the sync endpoint (#369).

**Architecture:**
- **Cooldown:** a new `FlightSync:ManualSyncCooldown` option lives on the API's `FlightSyncOptions` and reaches Application through `IFlightSyncOptions`.
  - It guards `SyncFlightStatusCommandHandler`: a `Traveler`-triggered sync of a flight whose `LastSyncedUtc` is newer than `now − cooldown` returns `false` with no provider call.
  - The cooldown is per flight, so every traveler tracking a flight shares it.
- **Rate limit:** a `flight-sync` fixed-window policy in `Program.cs`, partitioned by caller (`oid`, then `sub`, then IP), is attached to `POST /flights/{flightId}/sync`.

**Tech Stack:** .NET 10, ASP.NET Core rate limiting (`System.Threading.RateLimiting`), MediatR, xUnit, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`, `WebApplicationFactory` on the InMemory provider.

**Spec:** `docs/superpowers/plans/2026-09-15-flight-data-cost-reduction.md` (Phase 1, item 2) and issue #369.

## Global Constraints

- Work only in the worktree `/Users/salihalishov/Repositories/flight-assistant-backend-369`, branch `feature/369-manual-sync-cooldown`. Never touch the main checkout or other worktrees.
- Both sync endpoints keep their response `{ updated }`. No DTO, schema or migration change.
- The cooldown applies to `SyncTrigger.Traveler` only. `Admin` and `Scheduled` syncs are unchanged.
- The cooldown comparison is strict: skip only when `LastSyncedUtc > now − ManualSyncCooldown`. A flight that was never synced (`LastSyncedUtc` null) is never skipped, and `00:00:00` turns the cooldown off.
- Defaults:
  - `FlightSync:ManualSyncCooldown` = `00:05:00`, `[Range]` `00:00:00`–`01:00:00`.
  - `RateLimiting:FlightSync:PermitLimit` = `6`.
  - `RateLimiting:FlightSync:WindowSeconds` = `60`.
  - The class default and `appsettings.json` must agree; `AppSettingsJson_FlightSyncSection_MatchesTheCodeDefaults` pins this.
- Tests with option values use **non-default** values, so a hardcoded constant can't pass (#308).
- Make no call to AeroAPI or any other paid external API. Never run the API with sync on. Never run `dotnet ef database update`.
- Regenerate `spec/openapi.json` only with `FlightSync__Enabled=false scripts/generate-openapi.sh` from the worktree root; never hand-edit it.
- TDD: every new test is seen failing first. Each task ends with a prove step: break the code on purpose, watch the named test fail, restore. After restoring from a `.bak`, `touch` the file so MSBuild recompiles.
- Commit with explicit paths only (never `-A` or `.`). Every commit message ends with a blank line and `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`. Never push.

## Commands

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-369
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~SyncFlightStatusCommandHandlerTests"
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightSyncOptionsTests"
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightEndpointTests|FullyQualifiedName~AdminDataEndpointTests"
```

---

### Task 1: Cooldown on a traveler's manual sync

**Files:**
- Modify: `FlightAssistant.Application/FlightSync/IFlightSyncOptions.cs`
- Modify: `FlightAssistant.API/HostedServices/FlightSyncOptions.cs`
- Modify: `FlightAssistant.API/appsettings.json` (`FlightSync` section)
- Modify: `FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs` (`Handle`)
- Test: `FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs`
- Test: `FlightAssistant.API.Tests/HostedServices/FlightSyncOptionsTests.cs`
- Modify (compile only): the private `SyncOptions : IFlightSyncOptions` doubles in `FlightAssistant.Application.Tests/Commands/Flights/TrackFlightCommandHandlerTests.cs` and `FlightAssistant.Application.Tests/FlightSync/FlightSyncPlannerTests.cs`
- Modify: `CLAUDE.md`

**Interfaces:**
- Produces: `TimeSpan IFlightSyncOptions.ManualSyncCooldown { get; }` and `FlightSyncOptions.ManualSyncCooldown` (default 5 min). Task 2 doesn't consume it.

- [ ] **Step 1: Write the failing tests**

In `SyncFlightStatusCommandHandlerTests`:

(a) Give the `SyncOptions` double a settable, non-default cooldown (the other properties stay as they are):

```csharp
        public TimeSpan ManualSyncCooldown { get; init; } = TimeSpan.FromMinutes(4);
```

(b) Let the `Handler` factory take options:

```csharp
    private SyncFlightStatusCommandHandler Handler(
        ILogger<SyncFlightStatusCommandHandler>? logger = null, IFlightSyncOptions? syncOptions = null) =>
        new(_flights, _provider, syncOptions ?? new SyncOptions(), new WindowOptions(), _clock,
            logger ?? NullLogger<SyncFlightStatusCommandHandler>.Instance);
```

(c) Add these tests next to the `AdminSync_OfAFlightNotYetDue_StillCallsTheProvider` test:

```csharp
    // ── Traveler cooldown (#369) ──

    [Fact]
    public async Task TravelerSync_WithinTheCooldown_MakesNoCallAndSavesNothing()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        flight.MarkSynced(Now.AddMinutes(-3)); // the cooldown is 4 min

        Assert.False(await Sync(SyncTrigger.Traveler));

        await _provider.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await _flights.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(Now.AddMinutes(-3), flight.LastSyncedUtc);
    }

    [Theory]
    [InlineData(4)]  // exactly the cooldown ago: no longer within it
    [InlineData(10)]
    public async Task TravelerSync_CooldownOver_CallsTheProvider(int minutesSinceLastSync)
    {
        var flight = Given(Departing(Now.AddHours(1)));
        flight.MarkSynced(Now.AddMinutes(-minutesSinceLastSync));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(SyncTrigger.Traveler);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TravelerSync_NeverSynced_CallsTheProvider()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(SyncTrigger.Traveler);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdminSync_WithinTheTravelerCooldown_StillCallsTheProvider()
    {
        var flight = Given(Departing(Now.AddHours(1)));
        flight.MarkSynced(Now.AddMinutes(-1));
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Sync(SyncTrigger.Admin);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TravelerSync_CooldownZero_IsOff()
    {
        var handler = Handler(syncOptions: new SyncOptions { ManualSyncCooldown = TimeSpan.Zero });
        var flight = Given(Departing(Now.AddHours(1)));
        flight.MarkSynced(Now);
        _provider.GetStatusAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await handler.Handle(new SyncFlightStatusCommand(null, 1, SyncTrigger.Traveler), CancellationToken.None);

        await _provider.Received(1).GetStatusAsync("BA123", flight.ServiceDateUtc, Arg.Any<CancellationToken>());
    }
```

In `FlightSyncOptionsTests`:
- `Defaults_MatchTheSpec`: add `Assert.Equal(TimeSpan.FromMinutes(5), options.ManualSyncCooldown);`.
- `IFlightSyncOptions_ReflectsTheBoundSection`: add `["FlightSync:ManualSyncCooldown"] = "00:02:00",` to the settings and `Assert.Equal(TimeSpan.FromMinutes(2), options.ManualSyncCooldown);`.
- `AppSettingsJson_FlightSyncSection_MatchesTheCodeDefaults`: add `Assert.Equal(defaults.ManualSyncCooldown, shipped.ManualSyncCooldown);`.
- `AnOutOfRangeValue_FailsOnStart`: add `[InlineData("ManualSyncCooldown", "-00:01:00")]` and `[InlineData("ManualSyncCooldown", "01:00:01")]`.

- [ ] **Step 2: Run, expect a compile failure** (`ManualSyncCooldown` isn't on `IFlightSyncOptions` / `FlightSyncOptions`).

Run: `dotnet build`. Expected: build errors naming the missing `ManualSyncCooldown` member. Record them as the RED evidence.

- [ ] **Step 3: Implement**

`IFlightSyncOptions.cs`, add:

```csharp
        /// <summary>A traveler's manual sync within this long of the flight's last sync makes no
        /// provider call (#369).</summary>
        TimeSpan ManualSyncCooldown { get; }
```

`FlightSyncOptions.cs`, add after `StopWithoutArrivalAfter`:

```csharp
        /// <summary>A traveler's manual sync within this long of the flight's last sync makes no
        /// provider call (#369). Zero turns it off.</summary>
        [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
        public TimeSpan ManualSyncCooldown { get; set; } = TimeSpan.FromMinutes(5);
```

`appsettings.json`, add to the `FlightSync` section after `"StopWithoutArrivalAfter"`: `"ManualSyncCooldown": "00:05:00"`.

`TrackFlightCommandHandlerTests` and `FlightSyncPlannerTests`, add to each private `SyncOptions` double: `public TimeSpan ManualSyncCooldown => TimeSpan.FromMinutes(4);`.

`SyncFlightStatusCommand.cs`, in `Handle`, directly after the `Scheduled` still-due guard:

```csharp
            // A traveler pulling to refresh gets what the last sync stored: every call is billed, and
            // the loop already syncs a flight near departure every few minutes (#369). It reads the
            // flight's own LastSyncedUtc, so every traveler tracking the flight shares the cooldown.
            if (cmd.Trigger == SyncTrigger.Traveler && flight.LastSyncedUtc > now - _syncOptions.ManualSyncCooldown)
                return false;
```

- [ ] **Step 4: Run, expect PASS**. Then run the whole `FlightAssistant.Application.Tests` and `FlightAssistant.API.Tests` projects.

- [ ] **Step 5: Prove** (restore each with `mv … .bak` and `touch`)
  1. Remove `cmd.Trigger == SyncTrigger.Traveler &&`: `AdminSync_WithinTheTravelerCooldown_StillCallsTheProvider` fails.
  2. Change `>` to `>=`: `TravelerSync_CooldownOver_CallsTheProvider(4)` fails.
  3. Replace `_syncOptions.ManualSyncCooldown` with `TimeSpan.FromMinutes(5)`: `TravelerSync_CooldownOver_CallsTheProvider(4)` and `TravelerSync_CooldownZero_IsOff` fail.
  4. Set `appsettings.json`'s `"ManualSyncCooldown"` to `"00:10:00"`: `AppSettingsJson_FlightSyncSection_MatchesTheCodeDefaults` fails.

- [ ] **Step 6: CLAUDE.md**
  - At the end of the paragraph that starts `**Each tracked flight is synced on its own schedule (#356).**`, add: `A traveler's manual sync (SyncTrigger.Traveler) of a flight synced less than FlightSync:ManualSyncCooldown ago (default 5 min) returns false without calling the provider (#369). It reads the flight's own LastSyncedUtc, so every traveler tracking the flight shares the cooldown; admin and scheduled syncs ignore it.` Format the names as inline code, matching the paragraph.
  - In the Required Configuration row that starts `` | `FlightSync:LoopInterval` ``, append `` `ManualSyncCooldown` `` to the key list and `5 min (traveler manual-sync cooldown, #369)` to the defaults list. Leave the rest of the row unchanged.

- [ ] **Step 7: Commit**

```bash
git add FlightAssistant.Application/FlightSync/IFlightSyncOptions.cs FlightAssistant.API/HostedServices/FlightSyncOptions.cs \
  FlightAssistant.API/appsettings.json FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs \
  FlightAssistant.Application.Tests/Commands/Flights/SyncFlightStatusCommandHandlerTests.cs \
  FlightAssistant.Application.Tests/Commands/Flights/TrackFlightCommandHandlerTests.cs \
  FlightAssistant.Application.Tests/FlightSync/FlightSyncPlannerTests.cs \
  FlightAssistant.API.Tests/HostedServices/FlightSyncOptionsTests.cs CLAUDE.md
git commit -m "feat: a traveler's manual sync within 5 min of the last sync makes no provider call (#369)"
```

---

### Task 2: `flight-sync` rate limit on the traveler sync endpoint

**Files:**
- Modify: `FlightAssistant.API/Program.cs` (the `AddRateLimiter` block)
- Modify: `FlightAssistant.API/appsettings.json` (`RateLimiting` section)
- Modify: `FlightAssistant.API/Endpoints/FlightEndpoints.cs` (the `POST {flightId:int}/sync` endpoint)
- Test: `FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs`
- Modify: `CLAUDE.md`; `spec/openapi.json` (regenerated only)

**Interfaces:**
- Consumes: nothing from Task 1 at compile time. The tests sync a Schedule-mode flight (30 days out), which makes no provider call regardless of the cooldown.
- Produces: the `"flight-sync"` rate-limit policy name; `RateLimiting:FlightSync:PermitLimit` / `WindowSeconds`.

- [ ] **Step 1: Write the failing tests**

In `FlightEndpointTests` (add `using Microsoft.Extensions.Configuration;`), add a helper and refactor the existing `PostSync_TrackingTraveler_FlightDaysAway_Returns200WithUpdatedFalse_AndPlansTheFlight` to use it. Keep that test's assertions; its `departure.AddDays(-2)` becomes `flight.ScheduledDepartureUtc.AddDays(-2)`.

```csharp
    private int FlightSyncPermitLimit() =>
        factory.Services.GetRequiredService<IConfiguration>().GetValue("RateLimiting:FlightSync:PermitLimit", 6);

    /// <summary>A registered traveler tracking a flight 30 days out: Schedule mode, so a sync makes
    /// no provider call. A fresh traveler id each time, so tests never share a rate-limit bucket.</summary>
    private async Task<(HttpClient Client, Flight Flight)> TravelerTrackingAFlightDaysAwayAsync()
    {
        var userId = Guid.NewGuid();
        var client = factory.CreateUserClient(userId.ToString());
        await client.PostAsJsonAsync("travelers", new { email = $"{userId}@example.com", displayName = "Test", countryCode = (string?)null });

        var departure = DateTime.UtcNow.Date.AddDays(30).AddHours(9);
        var flight = Flight.Create(1, $"TK{Random.Shared.Next(1000, 9999)}", 10, 20, DateOnly.FromDateTime(departure), departure, departure.AddHours(3));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        db.Flights.Add(flight);
        await db.SaveChangesAsync();
        db.TravelerFlights.Add(TravelerFlight.Create(userId, flight.Id));
        await db.SaveChangesAsync();
        return (client, flight);
    }

    [Fact]
    public async Task PostSync_RateLimit_Returns429WithRetryAfter_AfterTheConfiguredPermitCount()
    {
        var (client, flight) = await TravelerTrackingAFlightDaysAwayAsync();

        for (var i = 0; i < FlightSyncPermitLimit(); i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"flights/{flight.Id}/sync", content: null)).StatusCode);

        var rejected = await client.PostAsync($"flights/{flight.Id}/sync", content: null);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
    }

    /// <summary>Partitioned by traveler (oid), not by IP: every test client shares one IP, so a second
    /// traveler getting 200 proves one traveler using up their permits doesn't throttle another.</summary>
    [Fact]
    public async Task PostSync_RateLimit_TwoTravelers_DoNotShareABucket()
    {
        var (clientA, flightA) = await TravelerTrackingAFlightDaysAwayAsync();
        var (clientB, flightB) = await TravelerTrackingAFlightDaysAwayAsync();

        for (var i = 0; i < FlightSyncPermitLimit(); i++)
            await clientA.PostAsync($"flights/{flightA.Id}/sync", content: null);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await clientA.PostAsync($"flights/{flightA.Id}/sync", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await clientB.PostAsync($"flights/{flightB.Id}/sync", content: null)).StatusCode);
    }
```

- [ ] **Step 2: Run, expect both new tests to FAIL** on the `TooManyRequests` assertion (the endpoint has no rate limit yet, so it returns 200). The refactored existing test still passes.

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightEndpointTests"`

- [ ] **Step 3: Implement**

`Program.cs`, inside `builder.Services.AddRateLimiter(rl => { … })`:

(a) The `oid` → `sub` → IP partition key is needed a third time, so extract it once at the top of the block and use it in `loyalty-programs`, `admin-reveal` and the new policy (their behaviour doesn't change):

```csharp
    // Authenticated policies partition by caller: oid, then sub, then IP (#318's reasoning — IP alone
    // would throttle everyone behind one NAT together and let a caller who rotates addresses dodge it).
    static string CallerPartitionKey(HttpContext ctx) =>
        ctx.User.FindFirst("oid")?.Value
        ?? ctx.User.FindFirst("sub")?.Value
        ?? ctx.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";
```

Replace the inline `partitionKey:` expressions of `loyalty-programs` and `admin-reveal` with `partitionKey: CallerPartitionKey(ctx),`. Keep their comments, except the sentence in `admin-reveal`'s that restates the fallback now documented on `CallerPartitionKey`; trim only that.

(b) After the `admin-reveal` policy:

```csharp
    // Traveler manual sync (#369). The per-flight cooldown (FlightSync:ManualSyncCooldown) already stops
    // repeat syncs of one flight reaching AeroAPI; this bounds how many requests one traveler can send
    // across all their flights. 6 per minute covers a few pulls to refresh.
    var syncLimit = builder.Configuration.GetValue("RateLimiting:FlightSync:PermitLimit", 6);
    var syncWindow = builder.Configuration.GetValue("RateLimiting:FlightSync:WindowSeconds", 60);
    rl.AddPolicy("flight-sync", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: CallerPartitionKey(ctx),
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = syncLimit,
            Window = TimeSpan.FromSeconds(syncWindow),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        }));
```

`appsettings.json`, add to the `RateLimiting` section after `"AdminReveal"`:

```json
    "FlightSync": {
      "PermitLimit": 6,
      "WindowSeconds": 60
    }
```

`FlightEndpoints.cs`, on the `POST {flightId:int}/sync` endpoint: add `.RequireRateLimiting("flight-sync")` after `.ProducesProblem(404)`, and replace the description with:

```csharp
            .WithDescription(
                "Triggers an on-demand status refresh for the specified flight. Whether the external flight data provider is queried depends on the flight's sync mode: " +
                "a flight not yet inside the provider's live-status window makes no provider call, and neither does a flight synced within the last 5 minutes. " +
                "Returns { updated: true } if a new status was recorded, or { updated: false } if nothing new was found or no call was made. " +
                "Rate-limited per traveler: too many sync requests return 429 with a Retry-After header. " +
                "Route parameter: flightId — the numeric flight ID.")
```

- [ ] **Step 4: Run, expect PASS** for `FlightEndpointTests` and `AdminDataEndpointTests` (the reveal rate-limit tests cover the extracted key). Then run the whole `FlightAssistant.API.Tests` project.

- [ ] **Step 5: Prove** (restore each with `mv … .bak` and `touch`)
  1. Remove `.RequireRateLimiting("flight-sync")`: both new tests fail.
  2. Give `flight-sync` an IP-only key (`partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"`): `PostSync_RateLimit_TwoTravelers_DoNotShareABucket` fails.
  3. Make `CallerPartitionKey` return `"unknown"`: `PostSync_RateLimit_TwoTravelers_DoNotShareABucket` and `Reveal_RateLimit_TwoDifferentActors_DoNotShareABucket` fail.

- [ ] **Step 6: CLAUDE.md**
  - After the #369 cooldown sentence added in Task 1, add: `POST /flights/{flightId}/sync also carries the flight-sync rate limit, partitioned by caller (oid → sub → IP) like loyalty-programs and admin-reveal; RateLimiting:FlightSync:PermitLimit and WindowSeconds default to 6 per 60 s.` Format the names as inline code.
  - In Required Configuration, after the `FlightSync:*` rows, add: `` | `RateLimiting:FlightSync:PermitLimit`, `WindowSeconds` | Optional; defaults 6 and 60. Requests one traveler may send to `POST /flights/{flightId}/sync` per window (#369) | ``

- [ ] **Step 7: Regenerate the OpenAPI spec**

```bash
FlightSync__Enabled=false scripts/generate-openapi.sh
git diff --stat spec/openapi.json
git diff spec/openapi.json
```

Expected: only the `SyncFlightStatus` operation's `description` changes. If anything else changes, stop and report.

- [ ] **Step 8: Full verification**

```bash
dotnet build --configuration Release 2>&1 | tail -3     # 0 errors, 0 warnings
dotnet test 2>&1 | grep -E "^(Passed|Failed)!|error"      # all four projects pass; record per-project counts
```

- [ ] **Step 9: Commit**

```bash
git add FlightAssistant.API/Program.cs FlightAssistant.API/appsettings.json FlightAssistant.API/Endpoints/FlightEndpoints.cs \
  FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs CLAUDE.md spec/openapi.json
git commit -m "feat: rate-limit a traveler's manual flight sync per traveler (#369)"
```

- [ ] **Step 10: Stop.** Don't push. The controller runs the final review and opens the PR.
