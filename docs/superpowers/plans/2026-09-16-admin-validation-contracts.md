# Admin Validation Contracts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Declare the 400 that ten admin routes already return, so the published contract stops omitting a status every one of them can produce.

**Architecture:** `.ProducesValidationProblem()` only, plus API-level tests. No handler logic changes, no description rewrites. The declarations render into `spec/openapi.json`, which CI regenerates and diffs, so the committed spec pins them and the tests pin the behaviour.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Swashbuckle, xUnit, WebApplicationFactory (InMemory).

**Spec:** issue #411 (the declarations half).

## Global Constraints

- **No behaviour change.** Only `.ProducesValidationProblem()` calls, new tests, and the regenerated spec. No handler, command, query, validator, repository or description string may change. If a fix appears to need one, **stop and report**.
- **The rule (CLAUDE.md, from #405):** declare every status **only this operation can produce** — thrown by its handler, raised by the validation pipeline, or returned by the endpoint itself. Never the ambient ones.
- **Never declare 401, the `AdminOnly` 403, or 429.** They are identical across the whole admin group. Adding them is the single worst thing this branch could do, because it would invert the rule it is applying.
- **Add nothing but the 400.** Every other declaration on these ten routes is already correct — verified route by route. Do not "tidy" a 404, a 409, or a 204.
- **`spec/openapi.json` will change** — a `"400"` appears on exactly ten operations. **Nothing else may move:** no schema, `$ref`, path, operationId, description, or any other status.
- The build stays warning-free; commits use explicit paths; no paid API calls, no migration, nothing pushed.

---

## File Structure

- **Modify** `FlightAssistant.API/Endpoints/Admin/AdminAirlineEndpoints.cs`, `AdminAirportEndpoints.cs`, `AdminLoungeEndpoints.cs`, `AdminLoyaltyEndpoints.cs`, `AdminTravelerEndpoints.cs`
- **Modify** `FlightAssistant.API.Tests/Integration/AdminEndpointContractTests.cs` — **create** it
- **Regenerate** `spec/openapi.json`

---

### Task 1: Declare the validators' 400 on ten admin routes

Verified against `origin/development` at `0d36009` before writing this plan. Every row below has a validator whose rule is reachable over HTTP, because `{id:int}` matches `0` and negatives and `{id:guid}` matches the all-zero guid (proved by `LoyaltyEndpointTests.RemoveMembership_EmptyGuid_Returns400`, added in #407).

| Route | Declares now | Validator rule | Add |
|---|---|---|---|
| `PUT /admin/airlines/{id:int}` | 204, 404 | `UpdateAirlineCommandValidator` | 400 |
| `DELETE /admin/airlines/{id:int}` | 204, 409 | `Id` `GreaterThan(0)` | 400 |
| `PUT /admin/airports/{id:int}` | 204, 404 | `UpdateAirportCommandValidator` | 400 |
| `DELETE /admin/airports/{id:int}` | 204 | `Id` `GreaterThan(0)` | 400 |
| `PUT /admin/lounges/{id:int}` | 204, 404 | `UpdateLoungeCommandValidator` | 400 |
| `DELETE /admin/lounges/{id:int}` | 204 | `Id` `GreaterThan(0)` | 400 |
| `PUT /admin/loyalty/programs/{id:int}` | 204, 404 | `UpdateLoyaltyProgramCommandValidator` | 400 |
| `DELETE /admin/loyalty/programs/{id:int}` | 204 | `Id` `GreaterThan(0)` | 400 |
| `DELETE /admin/travelers/{id:guid}` | 204 | `TravelerId` `NotEqual(Guid.Empty)` | 400 |
| `DELETE /admin/travelers/{travelerId:guid}/device-tokens/{tokenId:guid}` | 204, 404 | both ids `NotEqual(Guid.Empty)` | 400 |

**Deliberately NOT in scope, each checked and left alone:**

- `AdminGetHealth`'s declared **500** — the code comment says every known failure degrades into the 200 payload (including a database outage, from an earlier review's finding C1) and the 500 "stays reachable for anything unexpected, e.g. `HealthCheckService` itself throwing". A documented decision; not a sweep's to reverse.
- The **409** on `AdminAcceptVerificationSource` and `AdminRetryVerificationRequest` — both are genuinely thrown (six sites in `AcceptVerificationSourceCommand`, one at `RetryVerificationRequestCommand.cs:72`). They look absent from a naive grep only because the throws span multiple lines.
- `AdminDeleteFlight` declaring only 204 — correct: its handler returns early on a missing flight and the command has no validator.
- The `/admin/export` routes declaring no body contract at all — a different defect, raised as its own issue.

- [ ] **Step 1: Add the declaration to all ten routes**

In each file, add `.ProducesValidationProblem()` to the routes named above. Follow the order used elsewhere in the repository: `.Produces(...)` first, then `.ProducesValidationProblem()`, then any `.ProducesProblem(nnn)` in ascending order. For example, `DELETE /admin/airlines/{id:int}` becomes:

```csharp
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(409);
```

and `PUT /admin/airports/{id:int}` becomes:

```csharp
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(404);
```

Change nothing else on any route — no descriptions, no other statuses.

- [ ] **Step 2: Write the tests**

**Create** `FlightAssistant.API.Tests/Integration/AdminEndpointContractTests.cs`. It must carry `[Collection("Integration")]` and take `FlightAssistantWebApplicationFactory` as a primary-constructor parameter, matching `LoyaltyEndpointTests.cs` — every Integration class shares one host and one InMemory database, and a class without the attribute starts a second host against shared state.

The six `DELETE` routes need no body, so one theory covers them:

```csharp
    [Theory]
    [InlineData("admin/airlines/0")]
    [InlineData("admin/airports/0")]
    [InlineData("admin/lounges/0")]
    [InlineData("admin/loyalty/programs/0")]
    [InlineData("admin/travelers/00000000-0000-0000-0000-000000000000")]
    [InlineData("admin/travelers/00000000-0000-0000-0000-000000000000/device-tokens/00000000-0000-0000-0000-000000000000")]
    public async Task AdminDelete_NonPositiveOrEmptyId_Returns400(string url)
    {
        // Each of these routes' command ships a validator, and {id:int}/{id:guid} both match the
        // rejected values, so the 400 is reachable — it was simply undeclared (#411).
        var response = await factory.CreateAdminClient().DeleteAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
```

The four `PUT` routes need a **valid body with an invalid id**, so that the 400 is attributable to the id rule rather than to a malformed payload. Write them individually:

```csharp
    [Fact]
    public async Task AdminUpdateAirline_NonPositiveId_Returns400()
    {
        var response = await factory.CreateAdminClient().PutAsJsonAsync("admin/airlines/0", new
        {
            iata = "BA", icao = "BAW", name = "British Airways", countryCode = "GB", allianceCode = (string?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminUpdateAirport_NonPositiveId_Returns400()
    {
        var response = await factory.CreateAdminClient().PutAsJsonAsync("admin/airports/0", new
        {
            iata = "LHR", icao = "EGLL", name = "Heathrow", city = "London", countryCode = "GB",
            latitude = 51.47, longitude = -0.4543, elevationFt = 83
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminUpdateLounge_NonPositiveId_Returns400()
    {
        var response = await factory.CreateAdminClient().PutAsJsonAsync("admin/lounges/0", new
        {
            name = "Test Lounge", terminal = (string?)null, locationHint = (string?)null, hours = (string?)null,
            isAirside = true, isDomesticOnly = false, isInternationalOnly = false, amenities = (string?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminUpdateLoyaltyProgram_NonPositiveId_Returns400()
    {
        var response = await factory.CreateAdminClient().PutAsJsonAsync("admin/loyalty/programs/0", new
        {
            code = "BAEC", name = "Executive Club", kindId = 1, airlineId = (int?)null, allianceCode = (string?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
```

**Each `PUT` body must be valid apart from the id.** If any of these returns 400 for a body reason rather than the id rule, the test proves nothing — so verify by sending the **same body with a plausible non-zero id** and confirming you get something other than 400 (a 404 is expected, since the row will not exist). **Report that contrast for all four.** If any of them still 400s with a valid id, stop and report: the body is wrong and the test is not discriminating.

- [ ] **Step 3: Full check and commit**

```
dotnet test    # API gains 10 test cases (6 theory cases + 4 facts); report the totals you observe
dotnet build --configuration Release    # 0 errors, 0 warnings
scripts/generate-openapi.sh
git diff spec/openapi.json
```

The spec diff must show a `"400"` added on **exactly ten** operations and **nothing else changed** — no schema, `$ref`, path, operationId, description or other status. Confirm with:

```bash
git diff spec/openapi.json | grep -E '^[+-]' | grep -vE '^[+-][+-]' | grep -vE '"400"|"description"|\$ref|"content"|"application/problem|"schema"|^\s*[+-]\s*[\{\}],?\s*$'
```

which must print nothing, and count the added 400s:

```bash
git diff spec/openapi.json | grep -cE '^\+\s+"400"'   # expect 10
```

Re-confirm the conventions:

```bash
grep -rn "ProducesProblem(429)\|ProducesProblem(401)" --include='*.cs' FlightAssistant.API/   # nothing
grep -rn "ProducesProblem(403)" FlightAssistant.API/Endpoints/Admin/                          # nothing
```

```bash
git add FlightAssistant.API/Endpoints/Admin/AdminAirlineEndpoints.cs \
        FlightAssistant.API/Endpoints/Admin/AdminAirportEndpoints.cs \
        FlightAssistant.API/Endpoints/Admin/AdminLoungeEndpoints.cs \
        FlightAssistant.API/Endpoints/Admin/AdminLoyaltyEndpoints.cs \
        FlightAssistant.API/Endpoints/Admin/AdminTravelerEndpoints.cs \
        FlightAssistant.API.Tests/Integration/AdminEndpointContractTests.cs \
        spec/openapi.json
git commit -m "fix: declare the 400 ten admin routes already return (#411)"
```
