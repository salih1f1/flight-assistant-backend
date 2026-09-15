# Airport Timezone Lookup Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An airport whose timezone lookup fails costs at most one billed `GET /airports/{id}` call an hour, instead of one per search row (#373).

**Architecture:**
- **Lookup cache:** `AeroApiAirportTimezoneLookup` keeps a success for 365 days (was 30). It also keeps a failed lookup for 1 hour, stored as an empty-string marker under the same cache key.
- **Search filter:** `AeroApiFlightSearchProvider.FilterToIntendedLocalDateAsync`, the per-row filter used when a search's window is widened, resolves each distinct origin's zone once per search and reuses it for every row from that origin.
- **Unchanged:** search results, and the lookup's public signature.

**Tech Stack:** .NET 10, `ICacheService` (Redis or `IMemoryCache`), xUnit, NSubstitute.

**Spec:** `docs/superpowers/plans/2026-09-15-flight-data-cost-reduction.md` (Phase 1, item 4) and issue #373.

## Global Constraints

- Work only in the worktree `/Users/salihalishov/Repositories/flight-assistant-backend-373`, branch `feature/373-timezone-lookup-cache`. Never touch the main checkout or other worktrees.
- **Cache rules:**
  - A success is cached for exactly **365 days**.
  - A failed lookup is cached for exactly **1 hour**. That covers an unknown airport, a response with no usable `timezone` field, any non-success status, and an unreachable AeroAPI.
  - The failure marker is the empty string `""`. `GetAsync<string>` returns `null` for an absent key and `""` for the marker, in both `MemoryCacheService` and `RedisCacheService`, where JSON `""` round-trips.
  - The key prefix `aeroapi:airport-tz:` doesn't change, so zone ids cached before this change stay readable.
  - A cancelled call (`OperationCanceledException`) propagates and caches nothing.
- The TTLs are private constants in `AeroApiAirportTimezoneLookup`, like today's `CacheDuration`. No new options.
- `TryGetTimezoneAsync(string iata, CancellationToken ct = default)` keeps its signature and still never throws a `DomainException`.
- Search results don't change; only the number of `/airports` calls does. `ResolveWindowAsync` isn't touched.
- No API, DTO, schema, migration or `spec/openapi.json` change.
- Make no call to AeroAPI or any other paid external API: every test uses stub `HttpMessageHandler`s. Never run the API with sync on. Never run `dotnet ef database update`.
- The build stays warning-free (#301).
- TDD: every new test is seen failing first. Each task ends with a prove step: break the code on purpose, watch the named test fail, restore. After restoring from a `.bak`, `touch` the file so MSBuild recompiles.
- Commit with explicit paths only (never `-A` or `.`). Every commit message ends with a blank line and `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`. Never push.

## Commands

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-373
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiAirportTimezoneLookupTests"
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiFlightSearchProviderTests"
```

---

### Task 1: Cache failed lookups for an hour, successes for a year

**Files:**
- Modify: `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiAirportTimezoneLookup.cs`
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiAirportTimezoneLookupTests.cs`
- Modify: `CLAUDE.md`

**Interfaces:**
- Produces: the lookup behaviour Task 2 relies on. A failed lookup returns `null` and is cached for 1 hour. The signature doesn't change.

- [ ] **Step 1: Write the failing tests** — in `AeroApiAirportTimezoneLookupTests`:

(a) Update the class summary to: `pinning its contract: cache a success for 365 days and a failed lookup for 1 hour, never let a failure propagate, never cache a cancelled call.`

(b) Add usings `FlightAssistant.Infrastructure.Caching` and `Microsoft.Extensions.Caching.Memory`, and a second stub handler next to `StubHttpMessageHandler`:

```csharp
    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }
```

`Lookup(...)` takes a `StubHttpMessageHandler`. Change its parameter type to `HttpMessageHandler` so both stubs fit; existing callers still compile.

(c) Replace `TryGetTimezoneAsync_CachesTheResult_ForThirtyDays` with:

```csharp
    [Fact]
    public async Task TryGetTimezoneAsync_CachesASuccess_ForAYear()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "timezone": "Europe/Sofia" }""");
        var cache = EmptyCache();

        await Lookup(handler, cache).TryGetTimezoneAsync("SOF");

        await cache.Received(1).SetAsync(
            "aeroapi:airport-tz:SOF", "Europe/Sofia", TimeSpan.FromDays(365), Arg.Any<CancellationToken>());
    }
```

(d) Replace `TryGetTimezoneAsync_ReturnsNull_AndDoesNotCache_WhenTheAirportIsNotFound` with:

```csharp
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{ \"title\": \"not found\" }")]
    [InlineData(HttpStatusCode.TooManyRequests, "{ \"title\": \"too many requests\" }")]
    [InlineData(HttpStatusCode.Unauthorized, "{ \"title\": \"unauthorized\" }")]
    [InlineData(HttpStatusCode.OK, "{ \"code_iata\": \"ZZZ\" }")] // no timezone field
    public async Task TryGetTimezoneAsync_ReturnsNull_AndCachesTheMissForAnHour_WhenTheLookupFails(HttpStatusCode status, string body)
    {
        var handler = new StubHttpMessageHandler(status, body);
        var cache = EmptyCache();

        var timezone = await Lookup(handler, cache).TryGetTimezoneAsync("ZZZ");

        Assert.Null(timezone);
        await cache.Received(1).SetAsync(
            "aeroapi:airport-tz:ZZZ", "", TimeSpan.FromHours(1), Arg.Any<CancellationToken>());
    }
```

(e) Add:

```csharp
    [Fact]
    public async Task TryGetTimezoneAsync_CachesTheMissForAnHour_WhenAeroApiIsUnreachable()
    {
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException("connection refused"));
        var cache = EmptyCache();

        var timezone = await Lookup(handler, cache).TryGetTimezoneAsync("SOF");

        Assert.Null(timezone);
        await cache.Received(1).SetAsync(
            "aeroapi:airport-tz:SOF", "", TimeSpan.FromHours(1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGetTimezoneAsync_ReturnsNull_WithoutCallingTheApi_WhenAMissIsCached()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "timezone": "should-not-be-used" }""");
        var cache = Substitute.For<ICacheService>();
        cache.GetAsync<string>("aeroapi:airport-tz:ZZZ", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(""));

        var timezone = await Lookup(handler, cache).TryGetTimezoneAsync("ZZZ");

        Assert.Null(timezone);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TryGetTimezoneAsync_CachedMiss_SurvivesARealMemoryCache()
    {
        // Pins the marker against the real cache, not a substitute: an empty string must come back
        // as a cached miss, not as an absent key that triggers another billed call.
        var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, """{ "title": "not found" }""");
        var cache = new MemoryCacheService(new MemoryCache(new MemoryCacheOptions()));
        var lookup = Lookup(handler, cache);

        Assert.Null(await lookup.TryGetTimezoneAsync("ZZZ"));
        Assert.Null(await lookup.TryGetTimezoneAsync("ZZZ"));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TryGetTimezoneAsync_CachesNothing_WhenTheCallIsCancelled()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "timezone": "Europe/Sofia" }""");
        var cache = EmptyCache();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Lookup(handler, cache).TryGetTimezoneAsync("SOF", cts.Token));

        await cache.DidNotReceiveWithAnyArgs().SetAsync<string>(default!, default!, default, default);
    }
```

If `MemoryCacheService`'s constructor differs from `(IMemoryCache)`, or the test project can't see it, stop and report. Don't make the class public.

- [ ] **Step 2: Run, expect FAIL**
  - `CachesASuccess_ForAYear` fails (30 days).
  - All four `ReturnsNull_AndCachesTheMissForAnHour_WhenTheLookupFails` cases fail (nothing is cached).
  - `CachesTheMissForAnHour_WhenAeroApiIsUnreachable` fails.
  - `ReturnsNull_WithoutCallingTheApi_WhenAMissIsCached` fails (today `""` counts as absent, so it calls the API and returns the "should-not-be-used" value).
  - `CachedMiss_SurvivesARealMemoryCache` fails (two requests).
  - `CachesNothing_WhenTheCallIsCancelled` already passes; break 4 below is its proof.

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiAirportTimezoneLookupTests"`

- [ ] **Step 3: Implement** in `AeroApiAirportTimezoneLookup.cs`.

Replace `private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(30);` with:

```csharp
        // Airport time zones change about once a decade, so a success is kept for a year (#373).
        private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromDays(365);

        // A failed lookup (unknown airport, no timezone field, a throttled or failed call) is kept for
        // an hour, so an airport that keeps failing costs at most one billed call an hour instead of
        // one per search row (#373), while a transient failure still clears within the hour.
        private static readonly TimeSpan MissCacheDuration = TimeSpan.FromHours(1);

        // A cached miss is the empty string: GetAsync returns null for an absent key but "" for this
        // marker, in both MemoryCacheService and RedisCacheService (JSON "" round-trips). Zone ids
        // cached before #373 are non-empty strings, so they stay readable.
        private const string CachedMiss = "";
```

Replace the body of `TryGetTimezoneAsync` with:

```csharp
            var normalized = iata.ToUpperInvariant();
            var key = CacheKeyPrefix + normalized;

            var cached = await _cache.GetAsync<string>(key, ct);
            if (cached is not null)
                return cached.Length == 0 ? null : cached;

            var timezone = await FetchAsync(normalized, ct);

            if (string.IsNullOrWhiteSpace(timezone))
            {
                await _cache.SetAsync(key, CachedMiss, MissCacheDuration, ct);
                return null;
            }

            await _cache.SetAsync(key, timezone, SuccessCacheDuration, ct);
            return timezone;
```

In the class `<summary>`, replace `Results are cached under <c>ICacheService</c> for 30 days, successes only, because airport timezone assignments change on the order of once a decade.` with `A success is cached under <c>ICacheService</c> for 365 days, because airport timezone assignments change on the order of once a decade; a failed lookup is cached for 1 hour, so an airport that keeps failing costs at most one billed call an hour (#373).`

`FetchAsync` doesn't change: it already lets `OperationCanceledException` propagate.

- [ ] **Step 4: Run, expect PASS**, then run the whole `FlightAssistant.Infrastructure.Tests` project. Docker must be running, because its PostgreSQL tests use Testcontainers.

- [ ] **Step 5: Prove** (restore each with `mv … .bak` and `touch`)
  1. Cache the miss with `SuccessCacheDuration` instead of `MissCacheDuration`: `ReturnsNull_AndCachesTheMissForAnHour_WhenTheLookupFails` (all cases) and `CachesTheMissForAnHour_WhenAeroApiIsUnreachable` fail.
  2. Return the cached value as-is (`if (cached is not null) return cached;`): `ReturnsNull_WithoutCallingTheApi_WhenAMissIsCached` fails (returns `""`).
  3. Treat the marker as absent again (`if (!string.IsNullOrWhiteSpace(cached)) return cached;`): `ReturnsNull_WithoutCallingTheApi_WhenAMissIsCached` and `CachedMiss_SurvivesARealMemoryCache` fail.
  4. In `FetchAsync`, drop the `when (ex is not OperationCanceledException)` filter: `CachesNothing_WhenTheCallIsCancelled` fails.

- [ ] **Step 6: CLAUDE.md** — in the `**The local-day window shift.**` paragraph, replace `` `AeroApiAirportTimezoneLookup` (`GET /airports/{id}`, cached 30 days) `` with `` `AeroApiAirportTimezoneLookup` (`GET /airports/{id}`; a success is cached for 365 days and a failed lookup for 1 hour, #373) ``.

- [ ] **Step 7: Commit**

```bash
git add FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiAirportTimezoneLookup.cs \
  FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiAirportTimezoneLookupTests.cs CLAUDE.md
git commit -m "fix: cache a failed airport timezone lookup for an hour, a success for a year (#373)"
```

---

### Task 2: Look up each origin once per search

**Files:**
- Modify: `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightSearchProvider.cs` (`FilterToIntendedLocalDateAsync`, `TryGetLocalDateAsync`)
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiFlightSearchProviderTests.cs`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: Task 1's lookup. The tests use a cache substitute that always misses, so every count is a real lookup, and the count isolates this task's once-per-search behaviour.

- [ ] **Step 1: Write the failing tests** — in `AeroApiFlightSearchProviderTests`:

(a) Keep `TimezoneLookup(...)`, which has 15 callers, and move its body into a new helper that also returns the stub:

```csharp
    /// <summary>A lookup over <see cref="TimezoneStub"/>, returned with the stub so a test can count
    /// <c>/airports</c> calls. Its cache always misses, so every count is a real lookup.</summary>
    private static (AeroApiAirportTimezoneLookup Lookup, TimezoneStub Stub) CountingTimezoneLookup(
        params (string Iata, string? Tz)[] zones)
    {
        var cache = Substitute.For<ICacheService>();
        cache.GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var stub = new TimezoneStub(zones.ToDictionary(z => z.Iata, z => z.Tz));
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://stub/aeroapi/") };

        var lookup = new AeroApiAirportTimezoneLookup(
            http, cache, Options.Create(new AeroApiOptions { ApiKey = "dummy" }),
            NullLogger<AeroApiAirportTimezoneLookup>.Instance);
        return (lookup, stub);
    }

    private static AeroApiAirportTimezoneLookup TimezoneLookup(params (string Iata, string? Tz)[] zones) =>
        CountingTimezoneLookup(zones).Lookup;
```

(b) Add after `SearchAsync_KeepsTheRow_WhenTheOriginZoneNeverResolves`:

```csharp
    [Fact]
    public async Task SearchAsync_LooksUpAFailingOriginOnce_NotOncePerRow()
    {
        // A widened search (no origin in the criteria) whose rows all depart an airport the provider
        // knows no zone for. Each row used to trigger its own billed /airports lookup (#373).
        const string body = """
        {
          "scheduled": [
            { "ident_iata": "XX100", "actual_ident_iata": null, "origin_iata": "ZZZ", "destination_iata": "JFK",
              "scheduled_out": "2026-09-28T06:00:00Z", "scheduled_in": "2026-09-28T12:00:00Z" },
            { "ident_iata": "XX100", "actual_ident_iata": null, "origin_iata": "ZZZ", "destination_iata": "JFK",
              "scheduled_out": "2026-09-29T06:00:00Z", "scheduled_in": "2026-09-29T12:00:00Z" },
            { "ident_iata": "XX100", "actual_ident_iata": null, "origin_iata": "ZZZ", "destination_iata": "JFK",
              "scheduled_out": "2026-09-30T06:00:00Z", "scheduled_in": "2026-09-30T12:00:00Z" }
          ],
          "links": null,
          "num_pages": 1
        }
        """;
        var handler = new ScriptedHttpMessageHandler((HttpStatusCode.OK, body));
        var (timezones, stub) = CountingTimezoneLookup();

        var results = await Provider(handler, timezones).SearchAsync(
            new FlightSearchCriteria(null, null, new DateOnly(2026, 9, 29), "XX100"));

        // The UTC-date fallback still keeps only the 29th's row.
        var kept = Assert.Single(results);
        Assert.Equal(new DateTime(2026, 9, 29, 6, 0, 0, DateTimeKind.Utc), kept.ScheduledDepartureUtc);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task SearchAsync_LooksUpEachOriginOnce_AndStillFiltersEachRowByItsOwnZone()
    {
        // Two origins, two rows each. SOF resolves (Europe/Sofia, UTC+3 in September): its 06:35Z
        // departure on the 29th is kept, the 28th's isn't. ZZZ never resolves: the UTC-date fallback
        // keeps its 29th row and drops the 30th's.
        const string body = """
        {
          "scheduled": [
            { "ident_iata": "TK1028", "actual_ident_iata": null, "origin_iata": "SOF", "destination_iata": "IST",
              "scheduled_out": "2026-09-29T06:35:00Z", "scheduled_in": "2026-09-29T08:05:00Z" },
            { "ident_iata": "XX100", "actual_ident_iata": null, "origin_iata": "ZZZ", "destination_iata": "JFK",
              "scheduled_out": "2026-09-29T06:00:00Z", "scheduled_in": "2026-09-29T12:00:00Z" },
            { "ident_iata": "TK1028", "actual_ident_iata": null, "origin_iata": "SOF", "destination_iata": "IST",
              "scheduled_out": "2026-09-28T06:35:00Z", "scheduled_in": "2026-09-28T08:05:00Z" },
            { "ident_iata": "XX100", "actual_ident_iata": null, "origin_iata": "ZZZ", "destination_iata": "JFK",
              "scheduled_out": "2026-09-30T06:00:00Z", "scheduled_in": "2026-09-30T12:00:00Z" }
          ],
          "links": null,
          "num_pages": 1
        }
        """;
        var handler = new ScriptedHttpMessageHandler((HttpStatusCode.OK, body));
        var (timezones, stub) = CountingTimezoneLookup(("SOF", "Europe/Sofia"));

        var results = await Provider(handler, timezones).SearchAsync(
            new FlightSearchCriteria(null, null, new DateOnly(2026, 9, 29), "TK1028"));

        Assert.Equal(
            new[] { new DateTime(2026, 9, 29, 6, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 29, 6, 35, 0, DateTimeKind.Utc) },
            results.Select(r => r.ScheduledDepartureUtc));
        Assert.Equal(2, stub.Calls);
    }
```

- [ ] **Step 2: Run, expect both new tests to FAIL** on the call count: 3 and 4 today, against 1 and 2 expected. Their result assertions already pass, because the filtering logic doesn't change. The existing widened-window tests pass.

Run: `dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiFlightSearchProviderTests"`

- [ ] **Step 3: Implement** — in `AeroApiFlightSearchProvider.cs`, replace `FilterToIntendedLocalDateAsync` and `TryGetLocalDateAsync` with:

```csharp
        private async Task<List<Row>> FilterToIntendedLocalDateAsync(List<Row> rows, DateOnly date, CancellationToken ct)
        {
            // One lookup per distinct origin, not per row: otherwise an origin whose lookup fails costs a
            // billed /airports call for every one of its rows (#373). A miss is remembered too.
            var zones = new Dictionary<string, TimeZoneInfo?>(StringComparer.OrdinalIgnoreCase);

            var kept = new List<Row>();
            foreach (var row in rows)
            {
                if (!zones.TryGetValue(row.OriginIata, out var zone))
                {
                    zone = await TryResolveZoneAsync(row.OriginIata, ct);
                    zones[row.OriginIata] = zone;
                }

                if (zone is not null)
                {
                    var local = TimeZoneInfo.ConvertTimeFromUtc(
                        DateTime.SpecifyKind(row.ScheduledOutUtc, DateTimeKind.Utc), zone);
                    if (DateOnly.FromDateTime(local) == date) kept.Add(row);
                    continue;
                }

                // Still no zone available even now — approximate with the UTC calendar date rather
                // than drop the row outright. Wrong for a large-offset airport, but that is the
                // documented, logged cost of a genuinely unresolvable zone, not a silent loss.
                if (DateOnly.FromDateTime(row.ScheduledOutUtc) == date)
                    kept.Add(row);
            }
            return kept;
        }

        private async Task<TimeZoneInfo?> TryResolveZoneAsync(string originIata, CancellationToken ct)
        {
            var timezoneId = await _timezones.TryGetTimezoneAsync(originIata, ct);
            if (string.IsNullOrWhiteSpace(timezoneId)) return null;

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                _logger.LogWarning(ex, "Timezone id {TimezoneId} for origin {Origin} could not be loaded", timezoneId, originIata);
                return null;
            }
        }
```

Keep the method's existing `<summary>`. If `ResolveWindowAsync` or anything else calls `TryGetLocalDateAsync`, stop and report rather than changing that caller.

- [ ] **Step 4: Run, expect PASS** for `AeroApiFlightSearchProviderTests` and `AeroApiAirportTimezoneLookupTests`, then run the whole `FlightAssistant.Infrastructure.Tests` project.

- [ ] **Step 5: Prove** (restore each with `mv … .bak` and `touch`)
  1. Drop the dictionary and resolve per row (call `TryResolveZoneAsync` for every row): both new tests fail on the call count.
  2. Remember only resolved zones (`if (zone is not null) zones[row.OriginIata] = zone;` instead of always storing): both new tests fail on the call count, which shows a miss must be remembered too.
  3. Keep every row whose zone resolved (replace `if (DateOnly.FromDateTime(local) == date) kept.Add(row);` with `kept.Add(row);`): `SearchAsync_LooksUpEachOriginOnce_AndStillFiltersEachRowByItsOwnZone` fails on its results, and so does the existing `SearchAsync_WidensWindowAndFiltersPerRow_WhenNoOriginIsGiven`.

- [ ] **Step 6: CLAUDE.md** — in the `**The local-day window shift.**` paragraph, after the sentence ending `` each row is then filtered by its own `origin_iata`'s zone once the response supplies it. ``, add: `` Each distinct origin is looked up once per search, not once per row, so an origin whose lookup fails can't cost a billed call per row (#373). ``

- [ ] **Step 7: Full verification**

```bash
dotnet build --configuration Release 2>&1 | tail -3     # 0 errors, 0 warnings
dotnet test 2>&1 | grep -E "^(Passed|Failed)!|error"      # all four projects pass; record per-project counts
FlightSync__Enabled=false scripts/generate-openapi.sh && git diff --stat spec/openapi.json   # expect no diff
```

- [ ] **Step 8: Commit**

```bash
git add FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightSearchProvider.cs \
  FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiFlightSearchProviderTests.cs CLAUDE.md
git commit -m "fix: look up each origin's timezone once per search, not once per row (#373)"
```

- [ ] **Step 9: Stop.** Don't push. The controller runs the final review and opens the PR.
