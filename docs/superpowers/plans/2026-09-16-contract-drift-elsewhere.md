# Response-Code Contract Drift Elsewhere Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the rule #405 established to the four routes outside the flight surface that still declare statuses they cannot return, or omit ones they always do.

**Architecture:** `.Produces*` metadata and descriptions only, plus API-level tests. No handler logic changes. The declarations render into `spec/openapi.json`, which CI regenerates and diffs, so the committed spec pins the declarations and the tests pin the behaviour.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Swashbuckle, xUnit, WebApplicationFactory (InMemory).

**Spec:** issue #407, raised by #405's final review.

## Global Constraints

- **No behaviour change.** No handler, command, query, validator or repository may change. Only `.Produces*`, `.WithDescription` strings, new tests, and the regenerated spec. If a fix appears to need a handler change, **stop and report**.
- **The rule, already in CLAUDE.md from #405:** declare every status **only this operation can produce** — thrown by its handler, raised by the validation pipeline, or returned by the endpoint itself. Never declare the ambient ones.
- **The admin 403 is ambient — do NOT declare it.** `Program.cs:385` applies `.RequireAuthorization("AdminOnly")` to the entire `/admin` group, so a non-admin's 403 is uniform across every admin route. `AdminFlightEndpointTests` asserts it, which is correct as a *test*; declaring it per-route is what the rule forbids. Same for 401 and 429 everywhere.
- **`spec/openapi.json` will change** — `responses` objects on four operations. No schema, `$ref`, path or operationId may move.
- The build stays warning-free; commits use explicit paths; no paid API calls, no migration, nothing pushed.

---

## File Structure

- **Modify** `FlightAssistant.API/Endpoints/LoungeEndpoints.cs`
- **Modify** `FlightAssistant.API/Endpoints/LoyaltyEndpoints.cs`
- **Modify** `FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs`
- **Create** `FlightAssistant.API.Tests/Integration/LoungeEndpointTests.cs` — none exists
- **Modify** `FlightAssistant.API.Tests/Integration/LoyaltyEndpointTests.cs`, `AdminFlightEndpointTests.cs`
- **Regenerate** `spec/openapi.json`

---

### Task 1: Align the four routes and pin them

**Files:** all of the above.

Verified against `origin/development` at `96e76a7` before writing this plan:

| Route | Declares | Can actually return | Change |
|---|---|---|---|
| `GET /lounges` | 200, **404** | 200; 400 (`GetLoungesAtAirportQueryValidator`: `NotEmpty` + `^[A-Z]{3}$`) | drop 404, add 400 |
| `PUT /loyalty/memberships/{id}` | 204, 400, 404 | 204; 400; 404 (`:25`); **403** (`:28`) | add 403 |
| `DELETE /loyalty/memberships/{id}` | 204, **404** | 204; 400 (`NotEqual(Guid.Empty)`, and `{id:guid}` matches the empty guid); **403** (`:22`) | drop 404, add 403 + 400 |
| `POST /admin/flights/{id}/sync` | 200, 503 | 200; 400 (`FlightId > 0`); **404** (`SyncFlightStatusCommand.cs:78`); 500/502 (provider); 503 | add 404, 400, 500, 502 |

**Why the admin sync 404 is reachable when the traveler one was not:** the route sends `SyncFlightStatusCommand(null, id, SyncTrigger.Admin)`. With `TravelerId` null, the 403 gate at `:70-74` is skipped entirely and `GetFlightByIdAsync` at `:77-78` throws the 404. It is the mirror image of the traveler route, where the 403 gate always runs first.

**Why `DELETE /loyalty/memberships/{id}`'s 404 is unreachable:** `RemoveLoyaltyMembershipCommandHandler` reads `if (membership is null) return; // already removed — idempotent`. Same shape as `DELETE /flights/{id}`, which #405 corrected the same way.

- [ ] **Step 1: `GET /lounges`**

In `LoungeEndpoints.cs`, replace:

```csharp
            .Produces<IEnumerable<LoungeDto>>(200)
            .ProducesProblem(404);
```

with:

```csharp
            .Produces<IEnumerable<LoungeDto>>(200)
            .ProducesValidationProblem();
```

and add to the end of its `.WithDescription(...)`, before the closing `)`:

```csharp
                "An airport with no lounges is an empty list, not a 404 — this route never reports one. " +
                "Returns 400 if airportIata is missing or is not a 3-letter IATA code. " +
```

- [ ] **Step 2: `PUT /loyalty/memberships/{id}`**

Add the 403 it throws, keeping everything else:

```csharp
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(403)
            .ProducesProblem(404);
```

and add to its description:

```csharp
                "Returns 404 if no membership with that id exists, and 403 if it belongs to another traveler. " +
```

- [ ] **Step 3: `DELETE /loyalty/memberships/{id}`**

Replace:

```csharp
            .Produces(204)
            .ProducesProblem(404);
```

with:

```csharp
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(403);
```

and add to its description:

```csharp
                "Idempotent: removing a membership that does not exist also returns 204, so there is no 404. " +
                "Returns 403 if the membership belongs to another traveler. " +
```

Note the deliberate difference from `PUT` above, which *does* 404 for a missing membership — the same `DELETE`/write asymmetry #405 recorded for flights, and for the same reason.

- [ ] **Step 4: `POST /admin/flights/{id}/sync`**

Replace:

```csharp
            .Produces(200)
            .ProducesProblem(503);
```

with:

```csharp
            .Produces(200)
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(500)
            .ProducesProblem(502)
            .ProducesProblem(503);
```

and add to its description:

```csharp
                "Returns 404 if no flight with that id exists — unlike the traveler route, which answers 403 because it checks the tracking link first. " +
                "503 also covers the flight data provider throttling requests, not only the monthly budget; 502 is returned when the provider is unreachable or answers unexpectedly, and 500 for a provider configuration problem. " +
```

**Do not declare 403 here.** The `AdminOnly` policy makes it ambient across the whole admin group.

- [ ] **Step 5: Write the tests**

**Create** `FlightAssistant.API.Tests/Integration/LoungeEndpointTests.cs`, following the shape of `LoyaltyEndpointTests.cs` (same `FlightAssistantWebApplicationFactory` primary-constructor style, same collection attribute if that file has one — read it first and match it exactly):

```csharp
    [Fact]
    public async Task GetLounges_InvalidIataCode_Returns400()
    {
        var response = await factory.CreateUserClient().GetAsync("lounges?airportIata=LONDON");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetLounges_UnknownAirport_Returns200WithEmptyList_NotNotFound()
    {
        // The declared 404 was unreachable: this query never throws (#407).
        var response = await factory.CreateUserClient().GetAsync("lounges?airportIata=ZZZ");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
```

**Add to `LoyaltyEndpointTests.cs`:**

```csharp
    [Fact]
    public async Task RemoveMembership_Unknown_Returns204_Idempotently()
    {
        // The declared 404 was unreachable — the handler returns early (#407).
        var response = await factory.CreateUserClient().DeleteAsync($"loyalty/memberships/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task RemoveMembership_EmptyGuid_Returns400()
    {
        var response = await factory.CreateUserClient().DeleteAsync($"loyalty/memberships/{Guid.Empty}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
```

**Add to `AdminFlightEndpointTests.cs`**, beside `PostSync_Admin_UntrackedFlight_Returns200_WithOnlyUpdatedFalse`:

```csharp
    [Fact]
    public async Task PostSync_Admin_UnknownId_Returns404()
    {
        // Reachable here precisely because the admin route passes TravelerId: null, so the
        // traveler route's 403 gate is skipped and the flight lookup throws (#407).
        var response = await factory.CreateAdminClient().PostAsync("admin/flights/999999999/sync", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
```

**If any test observes a status other than the one asserted, stop and report it** — that means this plan's audit is wrong, which matters more than a green test. Do not adjust an assertion to match observed behaviour without saying so.

- [ ] **Step 6: Full check and commit**

```
dotnet test    # API gains 5 tests; report the totals you observe
dotnet build --configuration Release    # 0 errors, 0 warnings
scripts/generate-openapi.sh
git diff spec/openapi.json
```

The spec diff must be confined to `responses` objects and `description` strings on those four operations. Expected: `"404"` disappears from lounges and loyalty-delete and appears on admin sync; `"400"` appears on lounges, loyalty-delete and admin sync; `"403"` appears on both loyalty write routes; `"500"`/`"502"` appear on admin sync. Confirm no schema, `$ref`, path or operationId moved, and report the diff rather than pattern-matching it.

Also confirm the conventions still hold repo-wide:

```bash
grep -rn "ProducesProblem(429)\|ProducesProblem(401)" --include='*.cs' FlightAssistant.API/   # must print nothing
```

```bash
git add FlightAssistant.API/Endpoints/LoungeEndpoints.cs \
        FlightAssistant.API/Endpoints/LoyaltyEndpoints.cs \
        FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs \
        FlightAssistant.API.Tests/Integration/LoungeEndpointTests.cs \
        FlightAssistant.API.Tests/Integration/LoyaltyEndpointTests.cs \
        FlightAssistant.API.Tests/Integration/AdminFlightEndpointTests.cs \
        spec/openapi.json
git commit -m "fix: declare every status these routes can produce, and only those (#407)"
```
