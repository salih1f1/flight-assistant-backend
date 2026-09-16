# Remaining Tracked-Flight Slots Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a client learn a traveler's remaining tracking capacity from one call to `GET /flights`, instead of trying to derive it from one page of a paginated list.

**Architecture:** `GET /flights` already returns a per-flight `countsTowardCap` (#396). Add the traveler's *total* active count and the configured cap to the response envelope, taken from `IFlightRepository.CountActiveTrackedFlightsAsync` — the very method `POST /flights` enforces its 409 with — using the same single clock reading the per-row flags already use.

**Tech Stack:** .NET 10, EF Core, xUnit, NSubstitute, FakeTimeProvider.

**Spec:** issue #397, the follow-up raised by #396's final review.

## Global Constraints

- **The count comes from `CountActiveTrackedFlightsAsync`, never a reimplementation** and never by counting the page's `countsTowardCap` flags. The issue's acceptance is that the number agrees exactly with what `POST /flights` enforces; the only way that holds by construction is calling the same method with the same arguments.
- **One clock reading.** The count must use the same `now` local the per-row flags already use, not a second `GetUtcNow()`. Two readings can disagree across a stop boundary and produce a page whose flags contradict its own count.
- **JSON stays additive.** `items`, `total`, `page` and `pageSize` keep their names, types and order; the two new fields are appended. No existing client may break.
- **Existing tests keep compiling and passing, untouched.** The new Application result type must keep the member names `Items` and `Total`, because `GetTravelerFlightsQueryHandlerTests` reads them.
- **`spec/openapi.json` WILL change** — this is a response-shape change. Exactly one route's 200 schema reference and one new schema. If any *other* route's entry moves, stop and report.
- The build stays warning-free; commits use explicit paths; no paid API calls, no migration, nothing pushed.

---

## File Structure

- **Create** `FlightAssistant.Contracts/Responses/Flights/TrackedFlightsDto.cs` — the response envelope.
- **Modify** `FlightAssistant.Application/Queries/Flights/GetTravelerFlightsQuery.cs` — new result record, handler asks for the count.
- **Modify** `FlightAssistant.API/Endpoints/FlightEndpoints.cs` — return and document the new shape.
- **Modify** `FlightAssistant.Application.Tests/Queries/Flights/GetTravelerFlightsQueryHandlerTests.cs`, `FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs`.
- **Modify** `CLAUDE.md` — record the one deviation from the `PagedResult<T>` envelope.
- **Regenerate** `spec/openapi.json`.

---

### Task 1: Report slot usage on the tracked-flights list

**Files:** all of the above.

**Interfaces:**
- Consumes: `IFlightRepository.CountActiveTrackedFlightsAsync(Guid, DateTime, TimeSpan, TimeSpan, CancellationToken)`, `IFlightSyncOptions.StopAfterArrival` / `.StopWithoutArrivalAfter` / `.MaxActiveTrackedFlightsPerTraveler`.
- Produces: `TravelerFlightsPage` (Application) and `TrackedFlightsDto` (Contracts), each carrying `ActiveTrackedFlights` and `TrackingCap`.

- [ ] **Step 1: The Application result type**

In `GetTravelerFlightsQuery.cs`, add this record directly beneath `TravelerFlightResult`. It deliberately repeats the four paging members rather than deriving from `PagedResult<T>`: a derived positional record re-declaring inherited property names raises CS0108, and this build is warning-free.

```csharp
    /// <summary>
    /// A page of tracked flights plus the traveler's slot usage (#397). Not a <c>PagedResult</c>:
    /// the last two members describe the traveler, not the page, and a client cannot derive them
    /// from one page — future-dated cancelled flights sort first, so page 1 can be entirely
    /// non-counting while the slot-holding flights sit on page 2.
    /// </summary>
    public record TravelerFlightsPage(
        IReadOnlyList<TravelerFlightResult> Items,
        int Total,
        int Page,
        int PageSize,
        // Flights of this traveler the status sync still polls, across every page — the same count
        // POST /flights refuses a 409 against, from the same repository method.
        int ActiveTrackedFlights,
        // FlightSync:MaxActiveTrackedFlightsPerTraveler, so no client hardcodes it.
        int TrackingCap
    );
```

Change the query's return type:

```csharp
    public record GetTravelerFlightsQuery(Guid TravelerId, PagedRequest Paging) : IRequest<TravelerFlightsPage>;
```

and the handler's declaration to `IRequestHandler<GetTravelerFlightsQuery, TravelerFlightsPage>` with `public async Task<TravelerFlightsPage> Handle(...)`.

- [ ] **Step 2: Ask for the count, on the same clock**

In the handler, the `now` local already exists (`var now = _timeProvider.GetUtcNow().UtcDateTime;`). **Reuse it** — do not call the clock again. After the `foreach` that builds `results`, and before the return:

```csharp
            // The traveler's slot usage across every page, not just this one (#397). Deliberately the
            // same method TrackFlightCommand enforces its 409 with, called with the same clock reading
            // as the per-row flags above, so the count can never contradict the flags beside it.
            var activeTrackedFlights = await _flights.CountActiveTrackedFlightsAsync(
                query.TravelerId, now, _syncOptions.StopAfterArrival, _syncOptions.StopWithoutArrivalAfter, ct);
```

Replace the return with:

```csharp
            return new TravelerFlightsPage(
                results, total, query.Paging.Page, query.Paging.PageSize,
                activeTrackedFlights, _syncOptions.MaxActiveTrackedFlightsPerTraveler);
```

- [ ] **Step 3: The response contract**

Create `FlightAssistant.Contracts/Responses/Flights/TrackedFlightsDto.cs`:

```csharp
namespace FlightAssistant.Contracts.Responses.Flights
{
    /// <summary>
    /// A page of tracked flights, plus the traveler's slot usage against the tracking cap (#397).
    /// The first four members match <c>PagedResult&lt;T&gt;</c> exactly, so this is additive for
    /// existing clients.
    /// </summary>
    public record TrackedFlightsDto(
        IReadOnlyList<FlightDto> Items,
        int Total,
        int Page,
        int PageSize,
        int ActiveTrackedFlights,
        int TrackingCap
    );
}
```

- [ ] **Step 4: Return and document it**

In `FlightEndpoints.cs`, the `GET ""` handler currently ends with a `PagedResult<FlightDto>`. Replace that return:

```csharp
                return Results.Ok(new TrackedFlightsDto(
                    dtos, result.Total, result.Page, result.PageSize,
                    result.ActiveTrackedFlights, result.TrackingCap));
```

Change `.Produces<PagedResult<FlightDto>>(200)` to `.Produces<TrackedFlightsDto>(200)`.

Replace the final sentence of `.WithDescription(...)` — the one beginning "Note it reflects this page only" — with:

```csharp
                "activeTrackedFlights counts every flight of this traveler that still occupies a slot, across all pages, and trackingCap is the configured limit — " +
                "so remaining capacity is trackingCap minus activeTrackedFlights, without paging through the list. " +
                "Tracking a flight the traveler already tracks still succeeds at the cap.")
```

Add the `using FlightAssistant.Contracts.Responses.Flights;` import only if it is not already present (it is — `FlightDto` is already used).

- [ ] **Step 5: Write the failing tests**

**Application** (`GetTravelerFlightsQueryHandlerTests.cs`). The `SyncOptions` stub already uses non-default values (`StopAfterArrival` 20 min, `StopWithoutArrivalAfter` 12 h, cap 3) — that is what makes test 2 discriminating, so use them.

```csharp
    [Fact]
    public async Task ReportsTheTravelersSlotUsageAndTheConfiguredCap()
    {
        var travelerId = Guid.NewGuid();
        _flights.GetTravelerFlightsAsync(travelerId, 1, 50, Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<(TravelerFlight, Flight)>)[], 0));
        _refData.GetAirlineIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string>());
        _refData.GetAirportIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string>());
        _flights.CountActiveTrackedFlightsAsync(
            travelerId, Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _handler.Handle(new GetTravelerFlightsQuery(travelerId, new PagedRequest(1, 50)), CancellationToken.None);

        // Page 1 is empty, yet the traveler holds 2 of 3 slots — the exact case #397 exists for.
        Assert.Equal(2, result.ActiveTrackedFlights);
        Assert.Equal(3, result.TrackingCap);
    }

    [Fact]
    public async Task TheSlotCountUsesTheSameClockAndWindowsAsTheRowFlags()
    {
        var travelerId = Guid.NewGuid();
        _flights.GetTravelerFlightsAsync(travelerId, 1, 50, Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<(TravelerFlight, Flight)>)[], 0));
        _refData.GetAirlineIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string>());
        _refData.GetAirportIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string>());

        await _handler.Handle(new GetTravelerFlightsQuery(travelerId, new PagedRequest(1, 50)), CancellationToken.None);

        // The exact instant and the injected (non-default) windows: a second clock reading, or the
        // planner's production defaults, both fail here. This is what makes the returned count the
        // same rule POST /flights enforces rather than a lookalike.
        await _flights.Received(1).CountActiveTrackedFlightsAsync(
            travelerId, Now, TimeSpan.FromMinutes(20), TimeSpan.FromHours(12), Arg.Any<CancellationToken>());
    }
```

**API** (`FlightEndpointTests.cs`), beside `GetTrackedFlights_ValidToken_Returns200`. Read the cap from the running host rather than hardcoding it, as `AdminTravelerEndpointTests.cs:475` does:

```csharp
    [Fact]
    public async Task GetTrackedFlights_CarriesSlotUsageAndTheCap()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);

        await client.PostAsJsonAsync("travelers", new
        {
            email = $"{userId}@example.com",
            displayName = "Test",
            countryCode = (string?)null
        });

        var body = await client.GetFromJsonAsync<JsonElement>("flights?page=1&pageSize=10");

        var cap = factory.Services.GetRequiredService<IFlightSyncOptions>().MaxActiveTrackedFlightsPerTraveler;
        Assert.Equal(0, body.GetProperty("activeTrackedFlights").GetInt32());
        Assert.Equal(cap, body.GetProperty("trackingCap").GetInt32());
        // Additive: the paging members a client already reads are untouched.
        Assert.Equal(0, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
    }
```

Add whatever `using` lines this needs (`System.Text.Json`, `System.Net.Http.Json`, `Microsoft.Extensions.DependencyInjection`, `FlightAssistant.Application.FlightSync`) only if absent.

**Do not** write an API test that tracks flights up to the cap and expects a 409: `appsettings.Testing.json` caps the `flight-track` limiter at **2**, below the suite's tracking cap of **3**, so the third POST returns 429 before the cap is reached. Agreement with the 409 is pinned by test 2 instead — same method, same arguments.

- [ ] **Step 6: Run them**

Run each project's suite. Expected: PASS, and **every pre-existing test still passes untouched** — in particular the four existing `GetTravelerFlightsQueryHandlerTests`, which read `result.Items` and `result.Total` and must keep compiling against the new record.

- [ ] **Step 7: Prove each test bites**

Break, watch the named test fail, restore, and record the verbatim failing output for each.

1. Hardcode `20` (the production default cap) instead of `_syncOptions.MaxActiveTrackedFlightsPerTraveler` → `ReportsTheTravelersSlotUsageAndTheConfiguredCap` fails on `TrackingCap`.
2. Call `_timeProvider.GetUtcNow().UtcDateTime` a second time for the count instead of reusing `now` → `TheSlotCountUsesTheSameClockAndWindowsAsTheRowFlags` fails. **If the second reading happens to equal `Now` because the fake clock never advances, say so plainly** and instead prove it by passing `TimeSpan.FromMinutes(30)` (the production default) in place of `_syncOptions.StopAfterArrival`, which must fail the same test.
3. Drop `ActiveTrackedFlights` from the endpoint's `TrackedFlightsDto` construction (pass `0`) → `GetTrackedFlights_CarriesSlotUsageAndTheCap` fails.

- [ ] **Step 8: Document the deviation**

In `CLAUDE.md`, the **Paging:** paragraph states the output is `PagedResult<T>(Items, Total, Page, PageSize)`. Append one sentence recording the single exception and why, so the next person does not "restore consistency" by deleting it:

> `GET /flights` is the one route that returns a wider envelope (`TrackedFlightsDto`): it adds `activeTrackedFlights` and `trackingCap`, which describe the **traveler**, not the page. A client cannot derive them from one page — tracked flights sort newest-service-date first, so future-dated cancelled flights can fill page 1 with non-counting rows while the slot-holding flights sit on page 2 (#397). The count comes from `CountActiveTrackedFlightsAsync`, the same method `POST /flights` enforces its 409 with, on the same clock reading as the per-row `countsTowardCap` flags.

- [ ] **Step 9: Full check and commit**

Run: `dotnet test` (all four projects PASS) and `dotnet build --configuration Release` (0 warnings, 0 errors).

Run: `FlightSync__Enabled=false scripts/generate-openapi.sh` then `git diff --stat spec/openapi.json`. **A diff is expected here** — unlike most changes. Confirm with `git diff spec/openapi.json` that it is limited to: the `GET /flights` 200 schema reference changing from `FlightDtoPagedResult` to `TrackedFlightsDto`, the new `TrackedFlightsDto` schema, and the removal of `FlightDtoPagedResult` if nothing else references it. **If any other route's entry changes, stop and report.**

Note for the report: `FlightDtoPagedResult` was referenced by exactly one route before this change (verified on `origin/development`).

```bash
git add FlightAssistant.Contracts/Responses/Flights/TrackedFlightsDto.cs \
        FlightAssistant.Application/Queries/Flights/GetTravelerFlightsQuery.cs \
        FlightAssistant.API/Endpoints/FlightEndpoints.cs \
        FlightAssistant.Application.Tests/Queries/Flights/GetTravelerFlightsQueryHandlerTests.cs \
        FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs \
        CLAUDE.md spec/openapi.json
git commit -m "feat: report a traveler's remaining tracking slots on GET /flights (#397)"
```
