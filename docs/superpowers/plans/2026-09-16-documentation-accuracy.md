# Documentation Accuracy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make five documentation claims true — one that contradicts a field we now return, three that describe an ordering imprecisely, and two commands in our own guide that run zero tests.

**Architecture:** Prose only. No logic changes anywhere: string literals, code comments, XML doc comments and `CLAUDE.md`. Three of the strings are Swagger descriptions, so `spec/openapi.json` is regenerated.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Swashbuckle.

**Spec:** issue #403, batched with two corrections deliberately deferred from #394 and #399.

## Global Constraints

- **No behaviour change of any kind.** Nothing outside string literals, comments, XML docs and `CLAUDE.md` may change. If a fix appears to require a code change, stop and report.
- **Only the flight-**status-event** ordering claims change.** Most "newest first" phrases in this repository are **accurate** and must not be touched: the audit log orders by `ActionUtc`, verification requests by `RequestedUtc`, a traveler's tracked flights by service date, CI runs by their own time. Only status-event lists order by **id** — that is the imprecision being fixed. A diff touching any other "newest first" is wrong.
- **`spec/openapi.json` will change**, confined to exactly **three** `description` strings: `POST /flights`, `GET /flights/{flightId}/status`, `GET /admin/flights/{id}/detail`. **No `$ref`, schema, path or parameter may change.** If one does, stop and report.
- **This change has no test coverage by its nature** — prose is not testable, and no test in this repository pins a Swagger string (verified). The spec diff and the greps in Step 7 *are* the verification. Do not invent a test that asserts a description string; it would pin wording rather than behaviour and would have to be rewritten on every copy edit.
- The build stays warning-free; commits use explicit paths; no paid API calls, no migration, nothing pushed.

---

## File Structure

- **Modify** `FlightAssistant.API/Endpoints/FlightEndpoints.cs` — the tracking-cap sentence and the status-history description.
- **Modify** `FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs` — a route comment and the detail description.
- **Modify** `FlightAssistant.Application/Queries/Admin/Flights/GetAdminFlightDetailQuery.cs` — one XML doc line.
- **Modify** `FlightAssistant.Contracts/Responses/Admin/AdminFlightDetailDto.cs` — one XML doc line.
- **Modify** `CLAUDE.md` — the two broken test-filter commands.
- **Regenerate** `spec/openapi.json`.

---

### Task 1: Correct five documentation claims

**Files:** all of the above.

**Interfaces:** none — nothing this task touches is consumed by code.

- [ ] **Step 1: The tracking cap (#403)**

`FlightEndpoints.cs`, in `POST /flights`'s `.WithDescription(...)`. The current line renders the **compiled default** (20) while the running app enforces the **configured** value, and `GET /flights` now returns that configured value as `trackingCap` — so the two can visibly contradict each other (under the Testing profile they read 20 and 3).

Replace:

```csharp
                $"A traveler can track up to {new FlightSyncOptions().MaxActiveTrackedFlightsPerTraveler} flights at a time by default. " +
```

with:

```csharp
                "A traveler can track a limited number of flights at a time. GET /flights reports the limit in force as trackingCap, " +
                "and how many slots are in use as activeTrackedFlights (#397) — this description deliberately states no number, " +
                "because the cap is configuration and any number compiled in here would be wrong wherever it is overridden. " +
```

Note this removes the only interpolation in that string, so the `$` prefix on **that line** goes away — it is a plain string now. Leave every other line of the description exactly as it is.

**Then check whether `FlightSyncOptions` is still referenced in this file at all.** If it is not, remove the now-unused `using FlightAssistant.API.HostedServices;` **only if** the build warns about it; if the build is silent, leave the using alone and say so in your report. Do not remove a using the build does not object to.

**Leave the next sentence alone** ("by default 30 minutes after it lands or is cancelled, or a day after its arrival time"). Those defaults are also configurable, but unlike the cap they have no counterpart field in any response, so nothing can contradict them, and stripping every number would leave the description unhelpfully vague. Recorded as a deliberate boundary, not an oversight.

- [ ] **Step 2: The traveler-facing status history**

Same file, `GET /flights/{flightId}/status`. "the ordered list" states that an order exists without saying which one. Verified: `FlightRepository.GetFlightStatusEventsAsync` orders `OrderByDescending(e => e.Id)` and is **unbounded** (unlike the admin view, which is capped).

Replace:

```csharp
                "Returns the ordered list of status events recorded for the flight. " +
```

with:

```csharp
                "Returns the flight's full status-event history — every event, not a page — most recently recorded first. " +
                "The order is the order events were recorded, not effectiveAtUtc, which can move backwards when an estimate " +
                "returns to an earlier value; so the first row is the flight's current status. " +
```

- [ ] **Step 3: The admin route comment**

`AdminFlightEndpoints.cs`, the comment above `MapGet("{id:int}/detail", ...)`. Replace:

```csharp
            // GET /admin/flights/{id}/detail — the flight with its current status and recent
            // status-event history, newest first.
```

with:

```csharp
            // GET /admin/flights/{id}/detail — the flight with its current status and recent
            // status-event history, most recently recorded first (by id, #351 — not by effective
            // time, which can move backwards when an estimate returns to an earlier value).
```

- [ ] **Step 4: The admin detail description**

Same file, in that route's `.WithDescription(...)`. Replace:

```csharp
                "most recent status events, newest first — the record of what the sync actually saw. " +
```

with:

```csharp
                "most recent status events, most recently recorded first — the record of what the sync actually saw, in the order it saw it. " +
                "Not ordered by effectiveAtUtc, which can move backwards when an estimate returns to an earlier value. " +
```

**Keep the `$` interpolation of `GetAdminFlightDetailQueryHandler.MaxStatusHistory` on the line above.** That is a compile-time constant, not configuration — nothing can override it at runtime, so unlike Step 1's cap it cannot go stale. The distinction is the point: interpolating a constant is fine, interpolating a configurable default is the defect.

- [ ] **Step 5: The two XML doc comments**

In `GetAdminFlightDetailQuery.cs`, replace:

```csharp
    /// The flight with its current status and recent status-event history, newest first. Backs
```

with:

```csharp
    /// The flight with its current status and recent status-event history, most recently recorded
    /// first (by id, #351 — not by effective time, which can move backwards). Backs
```

In `AdminFlightDetailDto.cs`, replace:

```csharp
    /// <summary>The flight with its current status and recent status-event history, newest first.
```

with:

```csharp
    /// <summary>The flight with its current status and recent status-event history, most recently
    /// recorded first (by id, #351 — not by effective time, which can move backwards).
```

- [ ] **Step 6: The two broken commands in our own guide**

`CLAUDE.md`, the "Run a specific test class or test" block. **Both** documented forms match nothing in this solution — verified directly: `ClassName=FlightTests` and `Name~Create_NormalizesFlightNumber` each report "No test matches the given testcase filter" and run **zero** tests, while `FullyQualifiedName~` works for both a class (33 tests) and a single test (1 test). The documented command fails on its own worked example, and a run that matches nothing reads as success to anyone skimming for failures.

Replace:

```
# Run a specific test class or test
dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj --filter "ClassName=FlightTests"
dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj --filter "Name~Create_NormalizesFlightNumber"
```

with:

```
# Run a specific test class or test. FullyQualifiedName is the only matcher that works here —
# ClassName= and Name~ match nothing, run zero tests, and report no failure, so a filtered run
# with a typo (or either of those forms) looks exactly like a passing run.
dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj --filter "FullyQualifiedName~FlightTests"
dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj --filter "FullyQualifiedName~Create_NormalizesFlightNumber"
```

Run both new commands and confirm they report 33 and 1 test respectively. Record the output.

- [ ] **Step 7: Verify the blast radius**

Prose has no tests, so these greps are the verification. Run each and record the output:

```bash
# 1. No other "newest first" was touched: this must list ONLY the four spots above.
git diff -U0 | grep -n "newest first\|newest-first"

# 2. The accurate ones are still intact (audit, verification, traveler flights, CI):
grep -rn "newest first" --include='*.cs' FlightAssistant.API/Endpoints/Admin/AdminAuditEndpoints.cs \
  FlightAssistant.API/Endpoints/Admin/AdminTravelerEndpoints.cs \
  FlightAssistant.API/Endpoints/Admin/AdminVerificationEndpoints.cs

# 3. No logic changed — the diff must contain no line that is not a string, comment or markdown:
git diff --stat
```

- [ ] **Step 8: Full check and commit**

```
dotnet test    # all four projects; totals unchanged at 2178 (Domain 369, Application 868, Infrastructure 570, API 371)
dotnet build --configuration Release    # 0 errors, 0 warnings
FlightSync__Enabled=false scripts/generate-openapi.sh    # from the worktree root
git diff spec/openapi.json
```

The spec diff **is expected** and must contain **exactly three** changed `description` values — `POST /flights`, `GET /flights/{flightId}/status`, `GET /admin/flights/{id}/detail` — and nothing else. Confirm with:

```bash
git diff spec/openapi.json | grep -E '^[+-]' | grep -v '"description"' | grep -v '^[+-][+-]'
```

which must print **nothing**. If any `$ref`, schema or path line appears, stop and report.

```bash
git add FlightAssistant.API/Endpoints/FlightEndpoints.cs \
        FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs \
        FlightAssistant.Application/Queries/Admin/Flights/GetAdminFlightDetailQuery.cs \
        FlightAssistant.Contracts/Responses/Admin/AdminFlightDetailDto.cs \
        CLAUDE.md spec/openapi.json
git commit -m "docs: correct the tracking cap, status ordering and test filter claims (#403)"
```
