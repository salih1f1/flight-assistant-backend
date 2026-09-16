# Tracking-Cap Flag on GET /flights Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tell a client which tracked flights still occupy a slot against the per-traveler cap.

**Architecture:** Expose the sync planner's existing "stopped" rule, and use it from the tracked-flights query to compute one boolean per row. No new query: the rows already carry every column the rule reads.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, MediatR, xUnit, NSubstitute, `FakeTimeProvider`.

**Spec:** issue #396, following #381's deferred item.

## Global Constraints

- **Do not create a third definition of "still polled".** Two exist — `FlightRepository.CountActiveTrackedFlightsAsync` (SQL) and `FlightSyncPlanner.IsStopped` (in memory) — kept in agreement only by `FlightRepositoryTests.CountActiveTrackedFlightsAsync_CountsTheTravelersFlightsThePlannerStillSyncs`. **Reuse the planner's rule; never restate its conditions.**
- **No extra database query.** `GetTravelerFlightsAsync` already returns whole `Flight` entities.
- **No behaviour change** to tracking, the cap, the 409, or the planner's rules. Only a new read-only field.
- **`spec/openapi.json` is regenerated, never hand-edited**, and gains **exactly one property** on `FlightDto`. Anything else moving means stop and report.
- The build stays warning-free; commits use explicit paths; no paid API calls, no migration, nothing pushed.

---

## File Structure

- **Modify** `FlightAssistant.Application/FlightSync/FlightSyncPlanner.cs` — expose the rule.
- **Modify** `FlightAssistant.Application/Queries/Flights/GetTravelerFlightsQuery.cs` — the result record and the handler.
- **Modify** `FlightAssistant.Contracts/Responses/Flights/FlightDto.cs` — one field.
- **Modify** `FlightAssistant.API/Endpoints/FlightEndpoints.cs` — map it, and document it.
- **Modify** `FlightAssistant.Application.Tests/Queries/Flights/GetTravelerFlightsQueryHandlerTests.cs` — the construction site and new tests.
- **Modify** `spec/openapi.json` — regenerated.

---

### Task 1: Expose the rule and surface the flag

**Files:** all six above.

**Interfaces:**
- Consumes: `FlightSyncPlanner`, `FlightSyncPlanOptions.From`, `IFlightSyncOptions`, `IFlightStatusWindowOptions`, `TimeProvider`.
- Produces: `TravelerFlightResult.CountsTowardCap` and `FlightDto.CountsTowardCap`, both `bool`.

- [ ] **Step 1: Expose the planner's rule**

In `FlightSyncPlanner.cs`, `IsStopped` is `private static`. Make it public, renamed for the question callers actually ask, and have `Plan` call the new name so there is still **one** implementation:

```csharp
        /// <summary>
        /// Whether the status sync has stopped polling this flight — the rule that decides whether it still
        /// occupies a slot against <c>FlightSync:MaxActiveTrackedFlightsPerTraveler</c> (#380). Public because
        /// <c>GET /flights</c> reports it per flight (#396): the SQL predicate in
        /// <c>CountActiveTrackedFlightsAsync</c> is the same rule expressed for the database, and
        /// <c>FlightRepositoryTests</c> pins the two together — a third copy would be one too many.
        /// </summary>
        public static bool IsStopped(Flight flight, DateTime nowUtc, FlightSyncPlanOptions options)
```

Keep the body exactly as it is. Update `Plan`'s call if the name changed.

- [ ] **Step 2: Add the field to the query result**

In `GetTravelerFlightsQuery.cs`, add a last parameter to `TravelerFlightResult`:

```csharp
        bool Notify,
        // True while the status sync still polls this flight, so it occupies a slot against the
        // per-traveler cap (#396). Computed from FlightSyncPlanner's own rule, never restated here.
        bool CountsTowardCap
```

- [ ] **Step 3: Compute it in the handler**

The handler takes only `IFlightRepository` and `IRefDataRepository` today. Add `IFlightSyncOptions`, `IFlightStatusWindowOptions` and `TimeProvider`, in the style of `TrackFlightCommandHandler`:

```csharp
        public GetTravelerFlightsQueryHandler(
            IFlightRepository flights,
            IRefDataRepository refData,
            IFlightSyncOptions syncOptions,
            IFlightStatusWindowOptions windowOptions,
            TimeProvider timeProvider)
```

In `Handle`, before the loop:

```csharp
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var planOptions = FlightSyncPlanOptions.From(_syncOptions, _windowOptions);
```

and pass the computed value as the new last argument of each `TravelerFlightResult`:

```csharp
                    r.Tracking.Notify,
                    !FlightSyncPlanner.IsStopped(r.Flight, now, planOptions)
```

Read one clock value for the whole page, not one per row, so two flights on the same page can't be judged against different instants.

- [ ] **Step 4: Add the field to the DTO and map it**

In `FlightDto.cs`, add `bool CountsTowardCap` as the last positional parameter. In `FlightEndpoints.cs`'s `GET ""` mapping, pass `r.CountsTowardCap` as the last argument.

Then extend that endpoint's description — it currently reads:

```csharp
            .WithDescription("Returns a paginated list of flights the authenticated traveler is tracking. Supports ?page and ?pageSize (default 50, max 200).")
```

Replace with:

```csharp
            .WithDescription(
                "Returns a paginated list of flights the authenticated traveler is tracking. Supports ?page and ?pageSize (default 50, max 200). " +
                "countsTowardCap is true while the status sync still polls the flight, which is what occupies a slot against the tracking cap " +
                "POST /flights enforces; it turns false once the flight has landed or been cancelled for long enough, or its arrival time passed " +
                "without a landing being reported. Note it reflects this page only — a traveler's remaining slots cannot be counted from one page of a paginated list.")
```

- [ ] **Step 5: Fix the construction site and write the tests**

`GetTravelerFlightsQueryHandlerTests` is the only place outside DI that constructs the handler. Give it the three new arguments, using a `FakeTimeProvider` and simple option stubs in the style of `SyncFlightStatusCommandHandlerTests`' `SyncOptions`/`WindowOptions` private classes. **Do not change what the existing three tests assert** — only their construction.

Add two tests:

```csharp
    [Fact]
    public async Task AFlightTheSyncStillPolls_CountsTowardTheCap()
```

— an upcoming flight (arrival comfortably in the future): `CountsTowardCap` is `true`.

```csharp
    [Fact]
    public async Task AFlightTheSyncHasStopped_DoesNotCountTowardTheCap()
```

— a flight whose `ActualArrivalUtc` is older than `StopAfterArrival`: `CountsTowardCap` is `false`.

Use the same non-default option values the other test classes use, so a hardcoded default cannot pass.

- [ ] **Step 6: Run and prove**

Run: `dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~GetTravelerFlightsQueryHandlerTests"`
Expected: PASS, including the three pre-existing tests.

Break, watch the named test fail, restore with `mv … .bak` then `touch`. Record each.

1. Drop the `!` so the flag returns `IsStopped` directly → `AFlightTheSyncStillPolls_CountsTowardTheCap` fails.
2. Hardcode `true` → `AFlightTheSyncHasStopped_DoesNotCountTowardTheCap` fails.
3. Read the clock inside the loop instead of once before it → **no test will fail**; say so in the report rather than claiming one did. It is a correctness-of-reasoning point, not something the suite can observe.

- [ ] **Step 7: Full check and commit**

Run: `dotnet test` (all four projects PASS) and `dotnet build --configuration Release` (0 warnings, 0 errors).
Run: `FlightSync__Enabled=false scripts/generate-openapi.sh && git diff --stat spec/openapi.json` — expect **one property added to `FlightDto`** and nothing else. If more moves, stop and report.

```bash
git add FlightAssistant.Application/FlightSync/FlightSyncPlanner.cs FlightAssistant.Application/Queries/Flights/GetTravelerFlightsQuery.cs FlightAssistant.Contracts/Responses/Flights/FlightDto.cs FlightAssistant.API/Endpoints/FlightEndpoints.cs FlightAssistant.Application.Tests/Queries/Flights/GetTravelerFlightsQueryHandlerTests.cs spec/openapi.json
git commit -m "feat: tell clients which tracked flights count toward the cap (#396)"
```
