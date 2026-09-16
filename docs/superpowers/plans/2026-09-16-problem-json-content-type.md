# Problem Response Content Type Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make handler-produced problem responses carry `application/problem+json`, which `spec/openapi.json` has declared on 122 responses all along.

**Architecture:** One argument in `GlobalExceptionHandler.WriteAsync`, the single helper all four exception arms funnel through. Tests pin the media type at both the unit and endpoint level. **No spec change** — the declarations are already correct; only the server was wrong.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, xUnit, WebApplicationFactory.

**Spec:** issue #415.

## Global Constraints

- **The response body must not change.** Only the `Content-Type` header moves. `GlobalExceptionHandlerTests` asserts on `title`, `detail`, `traceId` and `errors`; every one of those must still pass unmodified.
- **Do not route through `IProblemDetailsService`.** `AddProblemDetails()` is registered (`Program.cs:129`), but handing the problem to that service lets ASP.NET apply its own defaults — filling `Type` and overwriting `Title` — which would change bodies. The minimal explicit content type is the correct fix here; record that as a decision, not an oversight.
- **`spec/openapi.json` must show NO diff.** `.Produces*` already generates `application/problem+json` from the declared `ProblemDetails`/`HttpValidationProblemDetails` types — that is exactly why the spec and the server disagreed. **If regenerating produces a diff, stop and report**: it would mean this change altered a declaration, which it must not.
- The build stays warning-free; commits use explicit paths; no paid API calls, no migration, nothing pushed.

---

## File Structure

- **Modify** `FlightAssistant.API/Middleware/GlobalExceptionHandler.cs` — one argument.
- **Modify** `FlightAssistant.API.Tests/Middleware/GlobalExceptionHandlerTests.cs` — unit-level media type.
- **Modify** `FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs` — endpoint-level media type.
- **Modify** `CLAUDE.md` — record the guarantee and the rejected alternative.

---

### Task 1: Send the media type the spec already promises

Verified against `origin/development` at `d6468ca` before writing this plan:

| Producer | Media type today | Spec says |
|---|---|---|
| `GlobalExceptionHandler` — validation 400, `DomainException` (403/404/409/500/502/503), CSV 400, unhandled 500 | `application/json` ❌ | `application/problem+json` |
| Framework model-binding 400 (`?limit=abc`) | `application/problem+json` ✅ | same |
| Endpoint `Results.NotFound()` | `application/problem+json` ✅ | same |

So two 404s one route apart currently disagree, and only the framework's matches the published contract.

- [ ] **Step 1: The fix**

In `GlobalExceptionHandler.cs`, `WriteAsync` currently ends:

```csharp
            return ctx.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: null, ct);
```

Change the last argument, and say why in a comment above the call:

```csharp
            // RFC 9457: a problem document is application/problem+json, which is what every
            // .Produces*/.ProducesProblem declaration already publishes for these responses (#415).
            // Passing null here sent application/json instead, so handler-produced problems
            // contradicted the spec while the framework's own (model binding, Results.NotFound())
            // matched it. Not routed through IProblemDetailsService deliberately: that would let
            // ASP.NET fill Type and overwrite Title, changing bodies this fix must leave alone.
            return ctx.Response.WriteAsJsonAsync(
                problem, problem.GetType(), options: null, contentType: "application/problem+json", ct);
```

All four arms funnel through this helper, so this one line covers every problem response the handler produces.

- [ ] **Step 2: Measure what the header actually becomes**

**Do not assume the charset.** The implicit path appends `; charset=utf-8`; an explicit content type may or may not. Before writing assertions, run one test and print the raw header value, and **report it verbatim**. Then write every assertion against the **media type only**:

```csharp
Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
```

`ContentType?.MediaType` ignores parameters, so it is correct either way — which is also how the repository's one existing content-type assertion is written (`AdminVerificationEndpointTests.cs:213`). Do not assert the raw header string.

- [ ] **Step 3: Unit-level test**

In `GlobalExceptionHandlerTests.cs`, add a theory covering all four arms. The existing `Sut(...)` helper gives a `DefaultHttpContext` whose `Response.ContentType` is readable after the handler runs:

```csharp
    [Fact]
    public async Task EveryProblemResponse_IsProblemJson()
    {
        // The spec declares application/problem+json on 122 responses; before #415 this handler
        // sent application/json, so handler-produced problems contradicted it while the
        // framework's own matched. All four arms funnel through one helper.
        var (handler, ctx) = Sut(Environments.Development);
        await handler.TryHandleAsync(ctx, PlanRestriction(), default);

        Assert.StartsWith("application/problem+json", ctx.Response.ContentType);
    }
```

Add equivalent coverage for the validation arm (a `FluentValidation.ValidationException`) and the unhandled arm (a plain `Exception`), following the shapes the existing tests in that file already use for each. `StartsWith` is deliberate here — it tolerates a charset suffix without asserting one either way.

- [ ] **Step 4: Endpoint-level test**

The unit test proves the helper; an endpoint test proves it survives the real pipeline. In `FlightEndpointTests.cs`:

```csharp
    [Fact]
    public async Task AnErrorResponse_IsProblemJson()
    {
        // End-to-end: a DomainException through GlobalExceptionHandler must carry the media type
        // the spec declares (#415). Framework-produced problems already did; handler-produced
        // ones did not.
        var response = await factory.CreateUserClient().GetAsync("flights/999999/status");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
```

That route's 403 is the `DomainException` path pinned by #405, so this test rides on behaviour already covered — it adds the media type, nothing else.

- [ ] **Step 5: Prove it bites**

Revert the `contentType` argument to `null`, run the new tests, and confirm they fail. Capture the verbatim output, restore, and confirm `git diff FlightAssistant.API/Middleware/GlobalExceptionHandler.cs` shows only the intended change.

- [ ] **Step 6: Record it**

In `CLAUDE.md`, beside the existing `GlobalExceptionHandler` bullets in the error-handling section, add:

> Every problem response carries **`application/problem+json`** (RFC 9457), whichever layer produced it — `GlobalExceptionHandler`, the validation pipeline, ASP.NET's model binding, or an endpoint's own `Results.NotFound()`. Before #415 the handler passed `contentType: null` to `WriteAsJsonAsync` and sent `application/json`, so handler-produced problems contradicted the 122 spec entries declaring otherwise while the framework's own matched them — two 404s one route apart disagreed. The handler deliberately does **not** route through `IProblemDetailsService`: that service would fill `Type` and overwrite `Title`, changing bodies that `GlobalExceptionHandlerTests` pins.

- [ ] **Step 7: Full check and commit**

```
dotnet test    # report the totals you observe
dotnet build --configuration Release    # 0 errors, 0 warnings
scripts/generate-openapi.sh
git diff --stat spec/openapi.json
```

The spec diff **must print nothing**. The declarations are unchanged — this fix makes the server match what they already said. A diff means something went wrong; stop and report.

```bash
git add FlightAssistant.API/Middleware/GlobalExceptionHandler.cs \
        FlightAssistant.API.Tests/Middleware/GlobalExceptionHandlerTests.cs \
        FlightAssistant.API.Tests/Integration/FlightEndpointTests.cs \
        CLAUDE.md
git commit -m "fix: send application/problem+json for handler-produced problems (#415)"
```
