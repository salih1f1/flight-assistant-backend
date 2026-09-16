# Response-Code Contracts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the flight routes' declared response codes match what the handlers actually return, and pin each one with an API-level test — so the drift cannot recur invisibly.

**Architecture:** `.Produces*` metadata and descriptions only; no handler logic changes. The declarations are rendered into `spec/openapi.json`, which CI regenerates and diffs, so the committed spec becomes the pin for the declarations while the new tests pin the runtime behaviour.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Swashbuckle, xUnit, WebApplicationFactory (InMemory).

**Spec:** issue #405, raised by #403's final review.

## Global Constraints

- **No behaviour change.** No handler, command, query or validator may change. Only `.Produces*` calls, `.WithDescription` strings, new tests, `CLAUDE.md`, and the regenerated spec.
- **Do NOT declare 429.** No route in this repository declares it (verified: zero occurrences); throttling is documented in description prose. That is a deliberate house convention — leave it.
- **Do NOT declare the ambient auth codes.** 401 and 403 from authentication/scope failure are declared on **zero** routes, because `VerifyUserHasAnyAcceptedScope` can produce a 403 on *every* authenticated route. Only a **business-rule** status a handler explicitly throws gets declared. This distinction is the core of the change — see the ruling in Step 6.
- **`spec/openapi.json` will change**, and unlike recent changes it changes `responses` objects, not just descriptions. Expected: 403 appears for the first time in the document; two unreachable 404s disappear; one 404 is added. **No schema, `$ref`, path or operationId may change.**
- The build stays warning-free; commits use explicit paths; no paid API calls, no migration, nothing pushed.

---

## File Structure

- **Modify** `FlightAssistant.API/Endpoints/FlightEndpoints.cs` — four routes' declarations and descriptions.
- **Modify** `FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs` — four new tests.
- **Modify** `CLAUDE.md` — record the declaration convention.
- **Regenerate** `spec/openapi.json`.

---

### Task 1: Align the declarations and pin them

**Files:** all of the above.

**Interfaces:** none — nothing here is consumed by code.

Verified against `origin/development` before writing this plan:

| Route | Declares | Handler actually throws |
|---|---|---|
| `GET /flights/{flightId}/status` | 200, **404** | **403** only (`GetFlightStatusQuery.cs:32`) — the 404 is unreachable |
| `POST /flights/{flightId}/sync` | 200, 404, 503 | **403** (`:74`) **and** 404 (`:78`) — the 404 is fine, the 403 is undeclared |
| `DELETE /flights/{flightId}` | 204, **404** | nothing — `UntrackFlightCommand.cs:18` returns early, "already untracked — idempotent" |
| `POST /flights` | 201, 400, 409 | **+404** (`TrackFlightCommand.cs:51,54,57`, unknown airline/airport) — undeclared |

`GlobalExceptionHandler.cs:45` sets `Status = de.StatusCode`, so a `DomainException`'s code reaches the client unchanged. `PATCH /flights/{flightId}/notify` is **correct as declared** (it really throws 404) — do not touch it.

- [ ] **Step 1: `GET /flights/{flightId}/status`**

Replace:

```csharp
            .Produces<IEnumerable<FlightStatusEventDto>>(200)
            .ProducesProblem(404);
```

with:

```csharp
            .Produces<IEnumerable<FlightStatusEventDto>>(200)
            .ProducesProblem(403);
```

and add to the end of that route's `.WithDescription(...)`, before the closing `)`:

```csharp
                "Returns 403 if the authenticated traveler does not track this flight — including when no flight with that id exists, " +
                "so the response does not reveal whether a flight id is real to someone who does not track it. There is no 404. " +
```

- [ ] **Step 2: `POST /flights/{flightId}/sync`**

Add `403` beside the existing declarations (keep the 404 — it is reachable, thrown when the flight itself is missing):

```csharp
            .Produces(200)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(503)
```

Add to that route's description:

```csharp
                "Returns 403 if the authenticated traveler does not track the flight, and 404 if no flight with that id exists. " +
```

- [ ] **Step 3: `DELETE /flights/{flightId}`**

Remove the unreachable declaration:

```csharp
            .Produces(204);
```

and make the contract explicit in the description:

```csharp
                "Idempotent: untracking a flight the traveler does not track also returns 204, so there is no 404. " +
```

**Do not change the handler.** Deleting something already absent succeeding is correct REST semantics and is deliberate (`UntrackFlightCommand.cs:18`). Note the neighbouring `PATCH .../notify` *does* throw 404 for the same condition — that asymmetry is intended (patching an absent resource is an error; deleting one is not) and is recorded in the ruling, not "fixed".

- [ ] **Step 4: `POST /flights`**

Add the undeclared 404:

```csharp
            .Produces(201)
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(409)
```

and add to the description:

```csharp
                "Returns 404 if the airline or either airport in the request is not known to the system. " +
```

- [ ] **Step 5: Write the failing tests**

In `FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs`. These pin **runtime behaviour**; the declarations are pinned by the committed spec, which CI diffs.

Note `403` is returned for an unknown flight id too, so the first and third tests need **no seeding at all**.

```csharp
    [Fact]
    public async Task GetFlightStatus_NotTracking_Returns403_NotFound404()
    {
        // 403 even for a flight id that does not exist: the API does not reveal whether an id is
        // real to a traveler who does not track it (#405). The declared 404 was unreachable.
        var client = factory.CreateUserClient();
        var response = await client.GetAsync("flights/999999/status");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UntrackFlight_NotTracking_Returns204_Idempotently()
    {
        // Deleting what is already absent succeeds (#405); the declared 404 was unreachable.
        var client = factory.CreateUserClient();
        var response = await client.DeleteAsync("flights/999999");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ToggleNotifications_NotTracking_Returns404()
    {
        // The neighbour of the DELETE above, and deliberately different: patching an absent
        // resource is an error. Pinned so the asymmetry is a decision, not an accident.
        var client = factory.CreateUserClient();
        var response = await client.PatchAsJsonAsync("flights/999999/notify", new { notify = true });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TrackFlight_UnknownAirline_Returns404()
    {
        // Undeclared before #405. One POST only: the Testing profile allows 2 before a 429.
        var client = factory.CreateUserClient();
        var tomorrow = DateTime.UtcNow.AddDays(1);
        var response = await client.PostAsJsonAsync("flights", new
        {
            flightNumber = "QQ123",
            serviceDate = DateOnly.FromDateTime(tomorrow).ToString("yyyy-MM-dd"),
            departureAirportIata = "LHR",
            arrivalAirportIata = "JFK",
            scheduledDepartureUtc = tomorrow.AddHours(9),
            scheduledArrivalUtc = tomorrow.AddHours(18)
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
```

Add any `using` these need only if absent. If `PatchAsJsonAsync` is unavailable, use `client.PatchAsync` with `JsonContent.Create(...)`.

**If any test returns something other than the asserted code, stop and report it** — that would mean the audit in this plan is wrong, which is far more important than making a test pass. Do not adjust an assertion to match observed behaviour without telling me.

- [ ] **Step 6: Record the convention in `CLAUDE.md`**

Add to the error-handling section a short paragraph:

> **What a route declares, and what it does not.** `.ProducesProblem` lists the statuses a **handler explicitly throws** — the business outcomes a client must branch on, like the 403 `GET /flights/{id}/status` returns for a flight the traveler does not track. It deliberately does **not** list statuses that can arise on every authenticated route: 401 and the scope-failure 403 from `VerifyUserHasAnyAcceptedScope`, and 429 from a rate limiter — those are documented in description prose instead, because declaring them per-route would add noise to every operation without telling a client anything route-specific. A declared status that no handler can produce is worse than an undeclared one: a generated client branches on a response it will never receive (#405).

- [ ] **Step 7: Full check and commit**

```
dotnet test    # all four projects; API gains 4 tests -> 375, total 2183
dotnet build --configuration Release    # 0 errors, 0 warnings
FlightSync__Enabled=false scripts/generate-openapi.sh
git diff spec/openapi.json
```

The spec diff **is expected** and must be confined to `responses` objects and `description` strings on those four operations: `"403"` appears (for the first time in the document), two `"404"` entries disappear, one `"404"` is added. Confirm no schema moved:

```bash
git diff spec/openapi.json | grep -E '^[+-]' | grep -vE '^[+-][+-]' | grep -vE '"(40[0-9]|50[0-9])"|"description"|\$ref.*ProblemDetails|"content"|"application/problem|"schema"|\{|\}'
```

Report whatever that prints rather than assuming it is empty — this diff is structurally larger than a description-only one, so judge it, don't pattern-match it.

```bash
git add FlightAssistant.API/Endpoints/FlightEndpoints.cs \
        FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs \
        CLAUDE.md spec/openapi.json
git commit -m "fix: declare the response codes the flight routes actually return (#405)"
```
