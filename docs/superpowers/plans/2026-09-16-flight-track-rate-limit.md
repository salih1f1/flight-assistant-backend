# Flight-Track Rate Limit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `POST /flights` the rate limiter every other traveler-facing write already has.

**Architecture:** A fifth fixed-window policy in `Program.cs`, identical in shape to the four that exist, partitioned by the same caller key, reading its ceiling from configuration, applied to one route.

**Tech Stack:** .NET 10, ASP.NET Core rate limiting, xUnit, `WebApplicationFactory` on the InMemory provider.

**Spec:** issue #390, following #381's own note: "A script can still keep 20 flights polled for up to a day each. That's bounded, and the #303 budget guard sits behind it; a tracking rate limit is a follow-up."

## Global Constraints

See `.superpowers/sdd/2026-09-16-flight-track-rate-limit/global-constraints.md`. The short version: mirror the existing policies exactly, reuse the shared `OnRejected`, take the ceiling from configuration, touch only the `POST ""` mapping, change no tracking behaviour, leave `spec/openapi.json` byte-identical, keep the build warning-free, and make no paid API call.

---

## File Structure

- **Modify** `FlightAssistant.API/Program.cs` — the `flight-track` policy, directly after the `flight-sync` one.
- **Modify** `FlightAssistant.API/appsettings.json` — `RateLimiting:FlightTrack` defaults.
- **Modify** `FlightAssistant.API/appsettings.Testing.json` — a low permit count, so the limiter trips well inside the test suite's tracking cap.
- **Modify** `FlightAssistant.API/Endpoints/FlightEndpoints.cs` — `.RequireRateLimiting("flight-track")` on `POST ""`.
- **Modify** `FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs` — a seeding helper and two tests.
- **Modify** `CLAUDE.md` — one sentence recording the limiter and why its test ceiling is lower.

---

### Task 1: Add the policy, apply it, and prove it

**Files:** all six above.

**Interfaces:**
- Consumes: the existing `CallerPartitionKey(HttpContext)` local function in `Program.cs`, and the shared `rl.OnRejected` handler.
- Produces: a `"flight-track"` policy name, and `RateLimiting:FlightTrack:PermitLimit` / `:WindowSeconds`.

- [ ] **Step 1: Add the policy**

In `FlightAssistant.API/Program.cs`, directly after the `flight-sync` policy block (the one whose comment begins "Traveler manual sync (#369)") and before the `// Add Retry-After header on 429` comment, insert:

```csharp
    // Tracking a flight (#390). Tracking makes no provider call — TrackFlightCommandHandler has no
    // provider — so this is not a spend path. What it bounds is the rate at which one caller creates
    // tracked flights, each of which the sync loop then polls: #380 caps how many a traveler may have
    // active at once, not how fast they can be added, and each attempt costs reference-data lookups and
    // writes. 10 per minute sits just above flight-sync's 6 and well under #380's cap of 20, so a person
    // adding the flights from a search result never notices it.
    var trackLimit = builder.Configuration.GetValue("RateLimiting:FlightTrack:PermitLimit", 10);
    var trackWindow = builder.Configuration.GetValue("RateLimiting:FlightTrack:WindowSeconds", 60);
    rl.AddPolicy("flight-track", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: CallerPartitionKey(ctx),
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = trackLimit,
            Window = TimeSpan.FromSeconds(trackWindow),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        }));
```

- [ ] **Step 2: Configure the defaults**

In `FlightAssistant.API/appsettings.json`, inside `RateLimiting`, after the `FlightSync` entry:

```json
    "FlightTrack": {
      "PermitLimit": 10,
      "WindowSeconds": 60
    }
```

In `FlightAssistant.API/appsettings.Testing.json`, add a `RateLimiting` section (the file has none today) beside the existing `FlightSync` override:

```json
  "RateLimiting": {
    "FlightTrack": {
      "PermitLimit": 2
    }
  }
```

Two, deliberately: the suite's `FlightSync:MaxActiveTrackedFlightsPerTraveler` is 3, and a low ceiling keeps the rate-limit test to a handful of requests. This mirrors why that cap is overridden in the same file — a non-default value proves the configured number reaches the limiter rather than a hardcoded one.

- [ ] **Step 3: Apply it to the route**

In `FlightAssistant.API/Endpoints/FlightEndpoints.cs`, on the `POST ""` mapping (the one `.WithName("TrackFlight")`), replace its terminating line

```csharp
            .ProducesProblem(409);
```

with

```csharp
            .ProducesProblem(409)
            .RequireRateLimiting("flight-track");
```

Take care: `.ProducesProblem(409)` appears on other endpoints too. This is the one immediately after `.ProducesValidationProblem()` in the `TrackFlight` chain.

- [ ] **Step 4: Write the failing tests**

In `FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs`, add the helper and both tests. This class has no `SeedAsync` helper — the other integration classes each define their own private one — so seed inline with a scope, the way `TravelerTrackingAFlightDaysAwayAsync` in this same file already does.

```csharp
    private int FlightTrackPermitLimit() =>
        factory.Services.GetRequiredService<IConfiguration>().GetValue("RateLimiting:FlightTrack:PermitLimit", 10);

    /// <summary>Registers a traveler and returns a client plus a body that tracks successfully. The
    /// airline and airports must be seeded: reference rows seeded with <c>HasData</c> never reach this
    /// InMemory database, which is shared across the Integration collection — hence the existence guards
    /// and the codes no other test uses.</summary>
    private async Task<(HttpClient Client, TrackFlightRequest Body)> TravelerReadyToTrackAsync()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            if (!db.Airlines.Any(a => a.Iata == "ZR"))
                db.Airlines.Add(Airline.Create("ZR", null, "Track Limit Air", "GB"));
            if (!db.Airports.Any(a => a.Iata == "ZRA"))
                db.Airports.Add(Airport.Create("ZRA", null, "Track Limit Origin", "Origin", "GB"));
            if (!db.Airports.Any(a => a.Iata == "ZRB"))
                db.Airports.Add(Airport.Create("ZRB", null, "Track Limit Destination", "Destination", "US"));
            await db.SaveChangesAsync();
        }

        var userId = Guid.NewGuid();
        var client = factory.CreateUserClient(userId.ToString());
        await client.PostAsJsonAsync("travelers", new { email = $"{userId}@example.com", displayName = "Test", countryCode = (string?)null });

        var serviceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        var body = new TrackFlightRequest(
            $"ZR{Random.Shared.Next(1000, 9999)}", serviceDate, "ZRA", "ZRB",
            serviceDate.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc),
            serviceDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));

        return (client, body);
    }

    [Fact]
    public async Task TrackFlight_RateLimit_Returns429WithRetryAfter_AfterTheConfiguredPermitCount()
    {
        var (client, body) = await TravelerReadyToTrackAsync();

        // The same flight every time: re-tracking one the traveler already tracks returns 201 and adds
        // nothing, so #380's cap (3 in appsettings.Testing.json) can never be what rejects these.
        for (var i = 0; i < FlightTrackPermitLimit(); i++)
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("flights", body)).StatusCode);

        var rejected = await client.PostAsJsonAsync("flights", body);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
    }

    /// <summary>Partitioned by traveler (oid), not by IP: every test client shares one IP, so a second
    /// traveler getting 201 proves one traveler's burst doesn't throttle another.</summary>
    [Fact]
    public async Task TrackFlight_RateLimit_TwoTravelers_DoNotShareABucket()
    {
        var (clientA, bodyA) = await TravelerReadyToTrackAsync();
        var (clientB, bodyB) = await TravelerReadyToTrackAsync();

        for (var i = 0; i < FlightTrackPermitLimit(); i++)
            await clientA.PostAsJsonAsync("flights", bodyA);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await clientA.PostAsJsonAsync("flights", bodyA)).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await clientB.PostAsJsonAsync("flights", bodyB)).StatusCode);
    }
```

Add any `using` the file lacks — it already has `System.Net`, `System.Net.Http.Json`, the Contracts request namespace, the Domain entities namespace, `Microsoft.Extensions.Configuration` and `Microsoft.Extensions.DependencyInjection`; `Airline`/`Airport` live in `FlightAssistant.Domain.ReferenceData.Entities`, which may need adding.

- [ ] **Step 5: Run them**

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightEndpointTests"`
Expected: PASS, including every pre-existing test in the class.

**If the first test's loop sees a 409 rather than 201**, stop and report: it would mean re-tracking is not idempotent the way #380's tests imply, and the test design needs revisiting rather than the cap being worked around.

- [ ] **Step 6: Prove each test bites**

Break, watch the named test fail, restore with `mv … .bak` then `touch`. Record each break, its failing output and the restore.

1. Remove `.RequireRateLimiting("flight-track")` from the route → `TrackFlight_RateLimit_Returns429WithRetryAfter_AfterTheConfiguredPermitCount` fails (every request returns 201).
2. Replace `CallerPartitionKey(ctx)` with a constant string in the new policy → `TrackFlight_RateLimit_TwoTravelers_DoNotShareABucket` fails.
3. Hardcode `PermitLimit = 10` instead of reading `trackLimit` → the first test fails, because the Testing override of 2 no longer reaches the limiter.

- [ ] **Step 7: Document it**

Two edits, both verified against the file at HEAD.

**1. A sentence this change makes false.** The #380 paragraph (`**A traveler's tracked flights are capped (#380).**`) currently ends:

```
It bounds how many flights one account keeps polled, not how often a traveler tracks: there is no tracking rate limit, and concurrent requests can overshoot the unlocked count.
```

Replace that sentence with:

```
It bounds how many flights one account keeps polled, not how often a traveler tracks — that is the `flight-track` rate limit's job (#390), 10 per 60 s partitioned by caller (oid → sub → IP), configured under `RateLimiting:FlightTrack` and applied to `POST /flights` alone; tracking makes no provider call, so what it bounds is the rate at which one caller creates flights the sync loop then polls. Concurrent requests can still overshoot the unlocked count, and like every policy the limiter counts per app instance.
```

Leave the rest of that paragraph as it is.

**2. The configuration table.** Find the row beginning `| `RateLimiting:FlightSync:PermitLimit`, `WindowSeconds` |` and add directly after it:

```markdown
| `RateLimiting:FlightTrack:PermitLimit`, `WindowSeconds` | Optional; defaults 10 and 60. Tracking attempts one caller may send to `POST /flights` per window (#390). The API test suite overrides the limit to 2 in `appsettings.Testing.json`, so the limiter trips below that suite's tracking cap of 3 |
```

Do **not** add a new paragraph elsewhere: the #380 paragraph and this table row are where a reader already looks for both facts.

- [ ] **Step 8: Full check and commit**

Run: `dotnet test` (all four projects PASS) and `dotnet build --configuration Release` (0 warnings, 0 errors).
Run: `FlightSync__Enabled=false scripts/generate-openapi.sh && git diff --stat spec/openapi.json` — **must print nothing**; a rate limit changes no schema.

```bash
git add FlightAssistant.API/Program.cs FlightAssistant.API/appsettings.json FlightAssistant.API/appsettings.Testing.json FlightAssistant.API/Endpoints/FlightEndpoints.cs FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs CLAUDE.md
git commit -m "feat: rate-limit POST /flights, the last traveler-facing write without a limiter (#390)"
```
