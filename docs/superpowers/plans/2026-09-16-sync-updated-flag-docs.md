# Sync `updated` Flag Documentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the published contract describe what the `updated` flag actually means, and fix one stale sentence in CLAUDE.md.

**Architecture:** Documentation only — three prose passages and the regenerated OpenAPI spec. No code path changes.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Swashbuckle-generated `spec/openapi.json`.

**Spec:** issue #394, following #382's deferred item.

## Global Constraints

- **No behaviour change of any kind.** No handler, no endpoint logic, no test assertion about behaviour. If a code change seems necessary, stop and report — this issue exists only because the documentation drifted from the code, and the code is correct.
- **`spec/openapi.json` is regenerated, never hand-edited:** `FlightSync__Enabled=false scripts/generate-openapi.sh` from the repo root. **Exactly two description strings change**; if anything else moves, stop and report it.
- **Don't lengthen these descriptions more than necessary.** Both are already long. Replace the wrong sentence; don't append a paragraph.
- **Don't change the flag or the response shape.** An iOS client consumes it; this makes the documentation true, nothing more.
- The build stays warning-free; commits use explicit paths; no paid API calls, no migration, nothing pushed.

---

## File Structure

- **Modify** `FlightAssistant.API/Endpoints/FlightEndpoints.cs` — the traveler sync description.
- **Modify** `FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs` — the admin sync description.
- **Modify** `CLAUDE.md` — the stale "newest-first" sentence.
- **Modify** `spec/openapi.json` — regenerated.

---

### Task 1: Correct the three passages

**Files:** all four above.

**Interfaces:** none — no code surface changes.

- [ ] **Step 1: The traveler sync endpoint**

In `FlightAssistant.API/Endpoints/FlightEndpoints.cs`, in the `POST {flightId:int}/sync` description, replace this line:

```csharp
                "Returns { updated: true } if a new status was recorded, or { updated: false } if nothing new was found or no call was made. " +
```

with:

```csharp
                "Returns { updated: true } when an event was recorded — a new status, or a gate, terminal or baggage-claim change on the current status — " +
                "and { updated: false } when none was: no call was made, nothing new was found, or the response carried only time changes. " +
                "{ updated: false } does not mean nothing changed: a moved estimated departure or arrival updates the flight and is visible immediately on GET /flights. " +
```

- [ ] **Step 2: The admin sync endpoint**

In `FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs`, in the `POST {id:int}/sync` description, replace this line:

```csharp
                "Otherwise returns { updated: true } if a new status was recorded. " +
```

with:

```csharp
                "Otherwise { updated: true } means an event was recorded — a new status, or a gate, terminal or baggage-claim change on the current status — " +
                "and { updated: false } means none was, which still allows a moved estimated time to have been saved. " +
```

Leave the sentences around it (the no-call cases, the budget rule) exactly as they are.

- [ ] **Step 3: The stale CLAUDE.md sentence**

In `CLAUDE.md`, in the paragraph beginning `**`GET /admin/flights/{id}/detail` derives "current status" from the newest `FlightStatusEvent`...**`, replace:

```
since `IFlightRepository.GetRecentFlightStatusEventsAsync` already returns events newest-first
```

with:

```
since `IFlightRepository.GetRecentFlightStatusEventsAsync` already returns events most recently recorded first (by id, #351 — not by effective time, which can move backwards when an estimate returns to an earlier value)
```

- [ ] **Step 4: Regenerate the spec**

From the repo root:

```bash
FlightSync__Enabled=false scripts/generate-openapi.sh
git diff --stat spec/openapi.json
```

Expected: **exactly two changed lines** — the `description` of `POST /flights/{flightId}/sync` and of `POST /admin/flights/{id}/sync`. Confirm with `git diff spec/openapi.json` that nothing else moved: no reordering, no other path, no schema or response-list change. **If anything else moves, stop and report it.**

- [ ] **Step 5: Verify against the code, not the prose**

This is the step that matters: the new wording must be *true*. Read `FlightAssistant.Application/FlightSync/FlightStatusApplier.cs` and confirm, quoting line numbers in your report:

- which two paths return `true` (a new status event; a detail change on the latest event's status), and
- that a response carrying only time changes returns `false` while still updating the flight through `ApplyProviderTimes`.

If either claim turns out false, **stop and report** rather than adjusting the wording to match a guess — the whole point is that the contract should match the code.

- [ ] **Step 6: Full check and commit**

Run: `dotnet test` (all four projects PASS — nothing here should affect a test, so an unexpected failure is a finding) and `dotnet build --configuration Release` (0 warnings, 0 errors).

```bash
git add FlightAssistant.API/Endpoints/FlightEndpoints.cs FlightAssistant.API/Endpoints/Admin/AdminFlightEndpoints.cs CLAUDE.md spec/openapi.json
git commit -m "docs: describe what the sync 'updated' flag actually means (#394)"
```
