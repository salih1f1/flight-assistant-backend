# Refresh a Retimed Flight's Schedule Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep a flight's stored scheduled departure and arrival current when the airline retimes it.

**Architecture:** The status provider already reports `scheduled_out`; add `scheduled_in` beside it, carry both on `FlightStatusResult`, and refresh them in `Flight.ApplyProviderTimes` next to the estimates. Both apply paths already call that method, so one change covers new-status and known-status responses.

**Tech Stack:** .NET 10, EF Core, xUnit, NSubstitute.

**Spec:** issue #399, following #384's deferred item.

## Global Constraints

- **Times only — never `ServiceDateUtc`.** It is part of flight identity (`ix_flight_lookup`, pinned by `FlightIndexConfigurationTests`) and is origin-local; the provider matches its AeroAPI row by converting `scheduled_out` into the origin's timezone. Re-keying a retimed flight is a separate, riskier problem and is out of scope.
- **A missing value must never overwrite a known schedule.** If the provider omits a scheduled time, keep the stored one — the same rule actual times already follow. This matters because the scheduled times are non-nullable.
- **No change to how the planner or the cap read these fields**, and no change to `GetOrCreateFlightAsync`.
- **`spec/openapi.json` must not change.** `FlightStatusResult` is not published, and `FlightDto` already exposes both scheduled times — only their *values* become correct.
- The build stays warning-free; commits use explicit paths; no paid API calls, no migration, nothing pushed.

---

## File Structure

- **Modify** `FlightAssistant.Application/Interfaces/ExternalServices/IFlightStatusProvider.cs` — one more optional member.
- **Modify** `FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightStatusProvider.cs` — read `scheduled_in`.
- **Modify** `FlightAssistant.Domain/Flights/Entities/Flight.cs` — refresh the stored times.
- **Modify** `FlightAssistant.Application/FlightSync/FlightStatusApplier.cs` — pass them through.
- **Modify** the Domain, Application and Infrastructure tests named below.

---

### Task 1: Carry the scheduled times and refresh them

**Files:** all of the above.

**Interfaces:**
- Consumes: `FlightStatusResult`, `Flight.ApplyProviderTimes`, `AeroApiFlightStatusProvider.MapToResult`.
- Produces: `FlightStatusResult.ScheduledArrivalUtc`, and `ApplyProviderTimes` accepting both scheduled times.

- [ ] **Step 1: Carry the scheduled arrival**

In `IFlightStatusProvider.cs`, `FlightStatusResult` already ends with `ScheduledDepartureUtc`. Add one more optional member after it:

```csharp
        DateTime? ScheduledDepartureUtc = null,
        // The provider's current scheduled arrival (#399), the counterpart of ScheduledDepartureUtc.
        // A retimed flight's stored schedule is refreshed from both, so the sync planner's windows and
        // the tracking cap's stop rule stop working from the times the flight was first tracked with.
        DateTime? ScheduledArrivalUtc = null
```

In `AeroApiFlightStatusProvider.MapToResult`, read it beside the existing `estimated_in`:

```csharp
            var scheduledIn = TryGetUtcDateTime(flight, "scheduled_in");
```

and pass `ScheduledArrivalUtc: scheduledIn` as the last argument of the returned `FlightStatusResult`.

- [ ] **Step 2: Refresh the stored schedule**

In `Flight.ApplyProviderTimes`, add two optional trailing parameters and apply them with the **keep-when-omitted** rule:

```csharp
        public void ApplyProviderTimes(
            DateTime? estimatedDepartureUtc,
            DateTime? estimatedArrivalUtc,
            DateTime? actualDepartureUtc,
            DateTime? actualArrivalUtc,
            string? equipmentAircraftIcao,
            DateTime nowUtc,
            DateTime? reportedScheduledDepartureUtc = null,
            DateTime? reportedScheduledArrivalUtc = null)
```

Inside, beside the existing `actualDeparture`/`actualArrival` locals:

```csharp
            // An airline retimes a flight, sometimes by hours (#399). The stored schedule is written once at
            // tracking and would otherwise stay wrong for the flight's whole life — the planner's Live window
            // reads it directly, and the tracking cap's stop rule reads the arrival. A provider that omits a
            // scheduled time keeps the stored one: these are non-nullable, and "not reported" is not "unknown".
            var scheduledDeparture = reportedScheduledDepartureUtc ?? ScheduledDepartureUtc;
            var scheduledArrival = reportedScheduledArrivalUtc ?? ScheduledArrivalUtc;
```

Add both to the `changed` test, and assign them before the `if (changed)`:

```csharp
            var changed = EstimatedDepartureUtc != estimatedDepartureUtc
                          || EstimatedArrivalUtc != estimatedArrivalUtc
                          || ActualDepartureUtc != actualDeparture
                          || ActualArrivalUtc != actualArrival
                          || ScheduledDepartureUtc != scheduledDeparture
                          || ScheduledArrivalUtc != scheduledArrival
                          || (equipmentAircraftIcao is not null && EquipmentAircraftIcao != equipmentAircraftIcao);
```

```csharp
            ScheduledDepartureUtc = scheduledDeparture;
            ScheduledArrivalUtc = scheduledArrival;
```

**`ApplyStatusUpdate` needs a real change here, not just a pass-through.** Verified at HEAD, its first line is:

```csharp
            ApplyProviderTimes(estimatedDepartureUtc, estimatedArrivalUtc, actualDepartureUtc, actualArrivalUtc, equipmentAircraftIcao, nowUtc);
```

It already takes `reportedScheduledDepartureUtc` (added in #352) but **does not forward it** — that parameter is used only further down, to count a delay's minutes. So:

1. Add `DateTime? reportedScheduledArrivalUtc = null` as a new last parameter, after `reportedScheduledDepartureUtc`.
2. Forward both to the call:

```csharp
            ApplyProviderTimes(estimatedDepartureUtc, estimatedArrivalUtc, actualDepartureUtc, actualArrivalUtc, equipmentAircraftIcao, nowUtc,
                reportedScheduledDepartureUtc, reportedScheduledArrivalUtc);
```

Leave the delay-minutes use of `reportedScheduledDepartureUtc` exactly as it is — it must keep counting from the provider's reported schedule, which is now also what gets stored.

**Watch one interaction:** `ApplyStatusUpdate` sets `UpdatedUtc = nowUtc` unconditionally on the line after that call, so on the new-status path `UpdatedUtc` moves regardless. The `changed` test inside `ApplyProviderTimes` only governs the known-status path. Don't "fix" that; it is existing behaviour and out of scope.

- [ ] **Step 3: Pass them through the applier**

In `FlightStatusApplier`, the known-status path calls `ApplyProviderTimes(...)` and the new-status path calls `ApplyStatusUpdate(...)`. Add `result.ScheduledDepartureUtc` and `result.ScheduledArrivalUtc` to both calls, as named arguments.

- [ ] **Step 4: Write the failing tests**

**Domain** (`FlightAssistant.Domain.Tests/Flights/FlightTests.cs`), in the `ApplyProviderTimes` section:

1. `ApplyProviderTimes_ARetimedFlight_UpdatesTheStoredSchedule` — both reported times differ from the stored ones: both are updated and `UpdatedUtc` moves.
2. `ApplyProviderTimes_AResponseWithoutScheduledTimes_KeepsTheStoredOnes` — both omitted: the stored values are unchanged and, with nothing else changing, `UpdatedUtc` does **not** move.
3. `ApplyProviderTimes_AnUnchangedSchedule_DoesNotMarkTheFlightUpdated` — the reported times equal the stored ones and nothing else changed: `UpdatedUtc` unchanged.

**Application** (`FlightAssistant.Application.Tests/FlightSync/FlightStatusApplierTests.cs`):

4. A known-status response carrying a later `scheduled_out` updates the flight's stored departure — proving the applier passes it through on the path that records no new status.

**Infrastructure** (`AeroApiFlightStatusProviderTests`): assert `GetStatusAsync` reports `ScheduledArrivalUtc` from `scheduled_in`, mirroring the existing `GetStatusAsync_ReportsTheScheduledDeparture` test.

- [ ] **Step 5: Run them**

Run each project's suite. Expected: PASS, and **every pre-existing test still passes** — in particular `Flight`'s existing `ApplyProviderTimes` tests, which assert that omitted actual times are kept.

- [ ] **Step 6: Prove each test bites**

Break, watch the named test fail, restore with `mv … .bak` then `touch`. Record each.

1. Assign `reportedScheduledDepartureUtc` directly instead of `?? ScheduledDepartureUtc` → `ApplyProviderTimes_AResponseWithoutScheduledTimes_KeepsTheStoredOnes` fails (it would null a non-nullable column, so this may surface as a compile error — if so, say that plainly and use `default` to make the break compile).
2. Leave the two new fields out of the `changed` test → `ApplyProviderTimes_ARetimedFlight_UpdatesTheStoredSchedule` fails on `UpdatedUtc`.
3. Drop `ScheduledArrivalUtc: scheduledIn` from the provider → the new Infrastructure test fails.
4. Drop the pass-through from the applier's known-status call → the new Application test fails.

- [ ] **Step 7: Document it**

In `CLAUDE.md`, the `**A status the flight already has still updates it (#351).**` paragraph lists what `ApplyProviderTimes` refreshes. Add the schedule to that list in one clause, noting the keep-when-omitted rule and why it matters (the planner's Live window and the cap's stop rule read these fields).

- [ ] **Step 8: Full check and commit**

Run: `dotnet test` (all four projects PASS) and `dotnet build --configuration Release` (0 warnings, 0 errors).
Run: `FlightSync__Enabled=false scripts/generate-openapi.sh && git diff --stat spec/openapi.json` — **must print nothing**.

```bash
git add FlightAssistant.Application/Interfaces/ExternalServices/IFlightStatusProvider.cs FlightAssistant.Infrastructure/ExternalServices/AeroApi/AeroApiFlightStatusProvider.cs FlightAssistant.Domain/Flights/Entities/Flight.cs FlightAssistant.Application/FlightSync/FlightStatusApplier.cs FlightAssistant.Domain.Tests/Flights/FlightTests.cs FlightAssistant.Application.Tests/FlightSync/FlightStatusApplierTests.cs FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/AeroApiFlightStatusProviderTests.cs CLAUDE.md
git commit -m "fix: refresh a flight's stored schedule when the airline retimes it (#399)"
```
