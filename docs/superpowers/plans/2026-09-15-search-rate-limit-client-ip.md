# Search Rate Limit by Caller, with the Real Client IP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The `flight-search` rate limit counts each caller separately. A signed-in traveler or admin is counted by who they are, and an anonymous caller by their real client IP, which App Service's forwarded headers carry (#371).

**Architecture:**
- **Forwarded headers:** `ForwardedHeadersOptions` is configured in `Program.cs` for `X-Forwarded-For` and `X-Forwarded-Proto`, with no known-proxy list and `ForwardLimit = 1`. `app.UseForwardedHeaders()` becomes the first middleware, so `HttpContext.Connection.RemoteIpAddress` is the client and `Request.Scheme` is `https` behind App Service.
- **Partition key:** the `flight-search` policy switches from `RemoteIpAddress` to the existing `CallerPartitionKey` (`oid`, then `sub`, then IP).

**Tech Stack:** .NET 10, ASP.NET Core (`Microsoft.AspNetCore.HttpOverrides`, `System.Threading.RateLimiting`), xUnit, `WebApplicationFactory` on the InMemory provider.

**Spec:** `docs/superpowers/plans/2026-09-15-flight-data-cost-reduction.md` (Phase 1, item 3) and issue #371.

## Global Constraints

- Work only in the worktree `/Users/salihalishov/Repositories/flight-assistant-backend-371`, branch `feature/371-search-rate-limit-client-ip`. Never touch the main checkout or other worktrees.
- Anonymous search stays: `GET /flights/search` keeps `.AllowAnonymous()`. Requiring sign-in is the user's open decision, not part of this plan.
- The limits don't change: `RateLimiting:FlightSearch:PermitLimit` 8, `WindowSeconds` 60. No response shape, DTO, schema or migration change.
- Forwarded headers:
  - `ForwardedHeaders = XForwardedFor | XForwardedProto`
  - `ForwardLimit = 1`
  - known proxies cleared, and known networks cleared (in .NET 10 use `KnownIPNetworks`; `KnownNetworks` is obsolete)
  - `UseForwardedHeaders()` runs before every other middleware
- **No test may reach the real search provider.** The test host doesn't replace `IFlightSearchProvider`, so a valid search would call AeroAPI. The rate-limit tests send searches the validator rejects: a date with no route and no flight number returns 400. Those still spend a permit, because the rate limiter runs before the endpoint.
- Make no call to AeroAPI or any other paid external API. Never run the API with sync on. Never run `dotnet ef database update`.
- Regenerate `spec/openapi.json` only with `FlightSync__Enabled=false scripts/generate-openapi.sh` from the worktree root; never hand-edit it.
- The build stays warning-free (#301).
- TDD: every new test is seen failing first. Each task ends with a prove step: break the code on purpose, watch the named test fail, restore. After restoring from a `.bak`, `touch` the file so MSBuild recompiles.
- Commit with explicit paths only (never `-A` or `.`). Every commit message ends with a blank line and `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`. Never push.

## Commands

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-371
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightSearchRateLimitTests"
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj
```

---

### Task 1: Apply App Service's forwarded headers

**Files:**
- Modify: `FlightAssistant.API/Program.cs` (a `ForwardedHeadersOptions` registration in the services section, next to `AddRateLimiter`; `app.UseForwardedHeaders()` at the top of the middleware pipeline)
- Create: `FlightAssistant.API.Tests/Integration/FlightSearchRateLimitTests.cs`
- Modify: `CLAUDE.md`

**Interfaces:**
- Produces: the test class `FlightSearchRateLimitTests` with helpers `FlightSearchPermitLimit()`, `RejectedSearch(string path, string? forwardedFor)` and `UniqueClientIp()`. Task 2 adds tests to the same class.

- [ ] **Step 1: Write the failing tests**

Create `FlightAssistant.API.Tests/Integration/FlightSearchRateLimitTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// The flight-search rate limit (#371). Every request here is a search the validator rejects (a date
/// with no route and no flight number): it returns 400 without reaching the search provider, which in
/// this host is the real AeroAPI one, but the rate limiter runs before the endpoint, so it still spends a
/// permit. Each test uses its own client addresses and travelers, because the limiter's state is shared
/// with every other test in the Integration collection.
/// </summary>
[Collection("Integration")]
public class FlightSearchRateLimitTests(FlightAssistantWebApplicationFactory factory)
{
    private int FlightSearchPermitLimit() =>
        factory.Services.GetRequiredService<IConfiguration>().GetValue("RateLimiting:FlightSearch:PermitLimit", 8);

    private static HttpRequestMessage RejectedSearch(string path, string? forwardedFor = null)
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var request = new HttpRequestMessage(HttpMethod.Get, $"{path}?date={date}");
        if (forwardedFor is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return request;
    }

    /// <summary>A documentation-range IPv6 address, unique enough that no other test shares its bucket.</summary>
    private static string UniqueClientIp() =>
        $"2001:db8::{Random.Shared.Next(1, 0xFFFF):x}:{Random.Shared.Next(1, 0xFFFF):x}";

    private async Task SpendAllPermitsAsync(HttpClient client, string path, string? forwardedFor = null)
    {
        for (var i = 0; i < FlightSearchPermitLimit(); i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(RejectedSearch(path, forwardedFor))).StatusCode);
    }

    // ── Forwarded headers (#371) ──

    [Fact]
    public void ForwardedHeaders_TrustOnlyTheEntryTheFrontEndAppends()
    {
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public async Task AnonymousSearch_DifferentClientAddresses_DoNotShareABucket()
    {
        var client = factory.CreateClient();
        var addressA = UniqueClientIp();
        var addressB = UniqueClientIp();

        await SpendAllPermitsAsync(client, "flights/search", addressA);
        var rejected = await client.SendAsync(RejectedSearch("flights/search", addressA));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(RejectedSearch("flights/search", addressB))).StatusCode);
    }

    [Fact]
    public async Task AnonymousSearch_AClientSuppliedForwardedEntry_DoesNotChooseTheBucket()
    {
        // App Service's front end appends the real client address to whatever X-Forwarded-For the client
        // sent. Only that rightmost entry may count, or a client could pick a fresh bucket per request.
        var client = factory.CreateClient();
        var realAddress = UniqueClientIp();

        await SpendAllPermitsAsync(client, "flights/search", $"{UniqueClientIp()}, {realAddress}");

        var spoofed = await client.SendAsync(RejectedSearch("flights/search", $"{UniqueClientIp()}, {realAddress}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, spoofed.StatusCode);
    }
}
```

- [ ] **Step 2: Run, expect FAIL**
  - `ForwardedHeaders_TrustOnlyTheEntryTheFrontEndAppends` fails, because the default options forward nothing.
  - `AnonymousSearch_DifferentClientAddresses_DoNotShareABucket` fails: without forwarding, every request comes from the test server's own address, so address B gets 429.
  - `AnonymousSearch_AClientSuppliedForwardedEntry_DoesNotChooseTheBucket` passes already, since everything shares one bucket. Break 2 in Step 5 is its proof.

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~FlightSearchRateLimitTests"`

- [ ] **Step 3: Implement**

`Program.cs`: add `using Microsoft.AspNetCore.HttpOverrides;`. Directly before `// Rate limiting — fixed-window per policy`, add:

```csharp
// App Service terminates TLS and proxies every request, so without the forwarded headers
// RemoteIpAddress and Scheme are its front end's, and every IP-partitioned rate limit puts all
// callers in one bucket (#371). The front end's addresses aren't fixed, so there is no known-proxy
// list; ForwardLimit = 1 reads only the rightmost X-Forwarded-For entry — the one the front end
// appended — so a client can't choose its own address by sending the header itself.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
```

In the middleware pipeline, make `app.UseForwardedHeaders();` the first line after `// 5. Middleware Pipeline`, before the Swagger `if` block, with the comment `// First: everything after it sees the client's address and scheme (#371).`.

If the SDK reports `KnownIPNetworks` as missing, stop and report; don't fall back to the obsolete `KnownNetworks`. The build must stay warning-free.

- [ ] **Step 4: Run, expect PASS**, then run the whole `FlightAssistant.API.Tests` project.

- [ ] **Step 5: Prove** (restore each with `mv … .bak` and `touch`)
  1. Remove `app.UseForwardedHeaders();`: `AnonymousSearch_DifferentClientAddresses_DoNotShareABucket` fails.
  2. Change `ForwardLimit = 1` to `ForwardLimit = 2`: `AnonymousSearch_AClientSuppliedForwardedEntry_DoesNotChooseTheBucket` fails (the spoofed entry picks a fresh bucket), and so does `ForwardedHeaders_TrustOnlyTheEntryTheFrontEndAppends`.
  3. Move `app.UseForwardedHeaders();` to just after `app.UseRateLimiter();`: `AnonymousSearch_DifferentClientAddresses_DoNotShareABucket` fails.

- [ ] **Step 6: CLAUDE.md** — after the `**Shared outbound throttle:**` paragraph, add:

```markdown
**Forwarded headers are applied first (#371).** The API runs on Linux App Service, which terminates TLS and proxies every request, so without `UseForwardedHeaders` `HttpContext.Connection.RemoteIpAddress` is App Service's front end and every IP-partitioned rate limit lumps all callers into one bucket. `ForwardedHeadersOptions` forwards `X-Forwarded-For` and `X-Forwarded-Proto` with no known-proxy list (the front end's addresses aren't fixed) and `ForwardLimit = 1`, so only the rightmost `X-Forwarded-For` entry — the one the front end appended — is used; a client sending its own header can't pick its bucket. `UseForwardedHeaders()` is the first middleware. Putting another proxy in front of App Service (Front Door, Application Gateway) would add a hop, and `ForwardLimit` and the proxy list would have to change with it.
```

- [ ] **Step 7: Commit**

```bash
git add FlightAssistant.API/Program.cs FlightAssistant.API.Tests/Integration/FlightSearchRateLimitTests.cs CLAUDE.md
git commit -m "feat: apply App Service's forwarded headers so rate limits see the client's address (#371)"
```

---

### Task 2: Partition `flight-search` by caller

**Files:**
- Modify: `FlightAssistant.API/Program.cs` (the `flight-search` policy and `CallerPartitionKey`)
- Modify: `FlightAssistant.API/Endpoints/FlightEndpoints.cs` (search description), `FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs` (admin search description)
- Test: `FlightAssistant.API.Tests/Integration/FlightSearchRateLimitTests.cs`
- Modify: `CLAUDE.md`; `spec/openapi.json` (regenerated only)

**Interfaces:**
- Consumes: Task 1's helpers in `FlightSearchRateLimitTests` and its forwarded-headers configuration. The tests give both callers the same forwarded address, so only a per-caller key can separate them.

- [ ] **Step 1: Write the failing tests** — add to `FlightSearchRateLimitTests`:

```csharp
    // ── Partitioned by caller (#371) ──

    [Fact]
    public async Task SignedInSearch_TwoTravelersFromOneAddress_DoNotShareABucket()
    {
        var sharedAddress = UniqueClientIp();
        var travelerA = factory.CreateUserClient(Guid.NewGuid().ToString());
        var travelerB = factory.CreateUserClient(Guid.NewGuid().ToString());

        await SpendAllPermitsAsync(travelerA, "flights/search", sharedAddress);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await travelerA.SendAsync(RejectedSearch("flights/search", sharedAddress))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await travelerB.SendAsync(RejectedSearch("flights/search", sharedAddress))).StatusCode);
    }

    [Fact]
    public async Task AdminSearch_TwoAdminsFromOneAddress_DoNotShareABucket()
    {
        var sharedAddress = UniqueClientIp();
        var adminA = factory.CreateAdminClient($"search-a-{Guid.NewGuid():N}");
        var adminB = factory.CreateAdminClient($"search-b-{Guid.NewGuid():N}");

        await SpendAllPermitsAsync(adminA, "admin/flights/search", sharedAddress);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await adminA.SendAsync(RejectedSearch("admin/flights/search", sharedAddress))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await adminB.SendAsync(RejectedSearch("admin/flights/search", sharedAddress))).StatusCode);
    }
```

If `admin/flights/search?date=…` with no route and no flight number doesn't return 400 (for example, if the admin route validates differently), stop and report rather than changing the request to a valid search.

- [ ] **Step 2: Run, expect both new tests to FAIL** on the second caller's assertion: with `flight-search` keyed by address, caller B shares caller A's exhausted bucket and gets 429.

- [ ] **Step 3: Implement**

`Program.cs`, inside `AddRateLimiter`:
- Move the `CallerPartitionKey` local function and its comment above the `flight-search` policy, and change the comment's opening to `// Policies partition by caller: oid, then sub, then IP (#318's reasoning …` (drop "Authenticated").
- Replace the `flight-search` comment's first line (`// Flight search — public, partition by client IP. …`) with `// Flight search — public, partition by caller (#371): a signed-in traveler or admin by identity, an anonymous caller by client IP (forwarded, see UseForwardedHeaders).`. Keep the rest of that comment, which explains the limit of 8 (#282).
- Change its key to `partitionKey: CallerPartitionKey(ctx),`.

`FlightEndpoints.cs`, search description: in the `Responses:` list, after the `503` line, add `"- 429: too many searches from this caller (per signed-in traveler, or per client IP when anonymous); retry after the period in the Retry-After header.\n"`. The `503` line currently ends the string without `\n`, so move the closing `")` to after the new line and give the `503` line a trailing `\n`.

`AdminFlightEndpoints.cs`, admin search description: replace `so it shares the flight-search rate limit.` with `so it shares the flight-search rate limit, counted per admin.`.

- [ ] **Step 4: Run, expect PASS** for `FlightSearchRateLimitTests`, `FlightEndpointTests` and `AdminDataEndpointTests` (their partition tests use `CallerPartitionKey`). Then run the whole `FlightAssistant.API.Tests` project.

- [ ] **Step 5: Prove** (restore each with `mv … .bak` and `touch`)
  1. Put the `flight-search` key back to `ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"`: both new tests fail.
  2. Make `CallerPartitionKey` skip `oid` and `sub` (return only the IP expression): both new tests fail, and so do `PostSync_RateLimit_TwoTravelers_DoNotShareABucket` and `Reveal_RateLimit_TwoDifferentActors_DoNotShareABucket`.

- [ ] **Step 6: CLAUDE.md** — at the end of the Task 1 `**Forwarded headers are applied first (#371).**` paragraph, add: `` The `flight-search` policy (public and admin search) partitions by caller through `CallerPartitionKey` — `oid`, then `sub`, then that forwarded client IP — so signed-in travelers and admins each get their own 8-per-minute bucket and anonymous callers are counted per address. ``

- [ ] **Step 7: Regenerate the OpenAPI spec**

```bash
FlightSync__Enabled=false scripts/generate-openapi.sh
git diff --stat spec/openapi.json
git diff spec/openapi.json
```

Expected: only the `SearchFlights` and `AdminSearchFlights` operations' `description` lines change. If anything else changes, stop and report.

- [ ] **Step 8: Full verification**

```bash
dotnet build --configuration Release 2>&1 | tail -3     # 0 errors, 0 warnings
dotnet test 2>&1 | grep -E "^(Passed|Failed)!|error"      # all four projects pass; record per-project counts
```

- [ ] **Step 9: Commit**

```bash
git add FlightAssistant.API/Program.cs FlightAssistant.API/Endpoints/FlightEndpoints.cs FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs \
  FlightAssistant.API.Tests/Integration/FlightSearchRateLimitTests.cs CLAUDE.md spec/openapi.json
git commit -m "feat: partition the flight search rate limit by caller (#371)"
```

- [ ] **Step 10: Stop.** Don't push. The controller runs the final review and opens the PR.
