# Switching the Background Jobs On and Off — Design Spec

**Date:** 2026-09-18
**Status:** Approved in discussion, pending review of this document
**Issue:** #471

---

## Overview

Two background jobs run on a timer: `FlightStatusSyncBackgroundService` (polls tracked flights,
spends AeroAPI budget) and `ReferenceVerificationBackgroundService` (works the verification queue,
also spends AeroAPI budget). Turning either off today means editing `FlightSync:Enabled` or
`Verification:Enabled` and redeploying.

So there is no way to stop a job that is misbehaving, looping on a bad row, or burning through the
month's AeroAPI budget, without a deploy. `/admin/health` will happily show you the problem and
offer nothing to do about it.

**The obstacle is the shape of the loops, not the flag.** Both services do this:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    if (!_options.Enabled)
    {
        _log.LogInformation("… is disabled via config.");
        return;                      // the hosted service TERMINATES
    }
    // … loop forever
}
```

`ExecuteAsync` returns, so the hosted service is finished for the life of the process. There is no
loop left to notice a flag changing. A runtime switch therefore requires restructuring both loops to
keep running and decide per tick — which is a change to live production code whose failure mode is
silent, and is the real work of this spec.

---

## Goals

- An admin can stop either job from the admin panel, and start it again, without a deploy.
- A job that is switched off spends no AeroAPI budget and does no work.
- The switch survives a restart and a deploy.
- `/admin/health` distinguishes "switched off" from "running" and from "broken".
- Manual, on-demand runs keep working while a job's scheduled loop is off.
- Every toggle is attributable: who, when, and optionally why.

## Non-goals

- Pausing an individual flight or an individual verification request.
- Scheduling a job to run only in a time window.
- Any change to what a job does while it is running.
- A kill switch for a run already in flight. A toggle takes effect at the next tick; it does not
  abort the current one.

---

## 1. Three layers of "enabled"

| Layer | Source | Who sets it | Can it start a job? |
|---|---|---|---|
| **Config ceiling** | `FlightSync:Enabled`, `Verification:Enabled` | deployment | yes, and it is the only thing that can |
| **Runtime switch** | `job_switches` table | an admin, from the panel | no — it can only hold a job down |
| **Effective** | `configEnabled && switchEnabled` | derived | — |

Config is a **hard ceiling**. `Enabled=false` means the job cannot run in this environment at all,
and the panel renders its toggle disabled with the reason shown rather than hiding it.

This asymmetry is deliberate. A config flag set false is a deployment-level decision — "this
environment does not spend AeroAPI money", "this instance does not run jobs" — and a browser click
must not be able to undo it. The runtime switch exists to stop things, urgently, not to provision
them.

### 1.1 What changes in the loops

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    // The config ceiling is still read once, and still terminates the service. Nothing can turn
    // this back on without a deploy, so there is no reason to keep a loop alive for it.
    if (!_options.Enabled) { … return; }

    using var timer = new PeriodicTimer(_options.LoopInterval, _timeProvider);
    while (await timer.WaitForNextTickAsync(stoppingToken))
    {
        if (!await _switch.IsEnabledAsync(JobNames.FlightSync, ct))
        {
            _heartbeat.MarkSwitchedOff();
            continue;                // no work, no AeroAPI call, loop stays alive
        }
        // … the existing cycle, unchanged
    }
}
```

The config branch keeps its current behaviour exactly — terminating — because nothing reachable from
the panel can lift it. Only the switch needs a live loop.

---

## 2. The heartbeat problem

Today, a disabled job leaves its heartbeat at `NeverRun`, and `/admin/health` renders "disabled"
from the config flag beside it. Both services carry an explicit comment saying so: the heartbeat is
left untouched *so the health endpoint can say "disabled" rather than "broken"*.

A loop that keeps spinning breaks that. It would either leave the heartbeat at `NeverRun` while
visibly alive, or start marking runs it is not performing.

So each heartbeat gains a switched-off state:

- `VerificationJobOutcome` already has `Paused` — but it means something else there: *the cycle ran
  and held work back because the AeroAPI budget is exhausted*. Reusing it would conflate "we chose
  not to spend" with "an operator stopped us", which are different situations with different
  remedies. A distinct `SwitchedOff` is added instead.
- `FlightSyncOutcome` is `{ NeverRun, Running, Success, Failed }` and gains `SwitchedOff` likewise.

`/admin/health` then reports three distinguishable states per job: **switched off by config**
(unchanged), **switched off by an operator** (new, and names who), and **running**.

---

## 3. The switch store

Table `job_switches`, one row per job:

| Column | Meaning |
|---|---|
| `job` | PK — `flight-sync`, `verification` |
| `enabled` | the runtime switch |
| `changed_utc` | when it was last flipped |
| `changed_by` | which admin flipped it |
| `reason` | optional free text, shown on the health page |

Persisted rather than in-memory: a toggle must survive a restart. Someone switching a job off at
2am because it is burning budget, and a deploy silently switching it back on, is the failure mode
an operator would least expect and least quickly notice.

Rows are seeded enabled, so an upgrade changes no behaviour.

### 3.1 Caching

`IJobSwitch.IsEnabledAsync` is called every tick — every minute for flight sync. It reads a value
that changes perhaps twice a month, so an uncached read would be a database round trip per tick
forever.

Cached for **10 seconds**. That bounds how long a toggle takes to take effect, which is the number
that matters to an operator pressing the button: worst case they wait ten seconds plus the rest of
the current tick. Short enough to feel immediate, long enough that the read cost disappears.

The cache is per-process. On a scaled-out App Service each instance picks the change up within its
own window; there is no coordination and none is needed, since the switch is advisory rather than a
lock.

### 3.2 Excluded from admin auditing

`JobSwitch` is added to `AdminAuditInterceptor`'s exclusion set — but for the opposite reason to
`DataSourceStatus`. That one is excluded because it is written constantly and automatically. This
one is excluded because its writes are audited **explicitly**, with the reason text attached, and
letting the interceptor also fire would produce two audit rows per toggle saying different things.

`AdminAuditInterceptorTests` pins the exclusions (#469), so this gets a test there alongside them.

---

## 4. API

### `GET /admin/jobs`

```
[ { job, displayName, configEnabled, switchEnabled, effectiveEnabled,
    canToggle, changedUtc, changedBy, reason, lastOutcome, lastRunUtc } ]
```

`canToggle` is false exactly when `configEnabled` is false. The panel renders the control disabled
and shows why, rather than hiding it — a missing control makes an operator hunt for a feature that
is present but barred.

### `PUT /admin/jobs/{job}`

Body `{ enabled, reason? }`. AdminOnly.

- `404` — unknown job key
- `409` — `configEnabled` is false, with a message naming the config key to change. Not `403`: the
  caller is permitted, the environment is what refuses.
- `200` — the refreshed row

Writes an `AdminAuditLog` row explicitly, carrying the job, the new state and the reason. A job
being off for six hours must be answerable after the fact.

---

## 5. Manual triggers are unaffected

`POST /admin/flights/{id}/sync` and the manual verification endpoints do **not** consult the switch.
They already run through MediatR independently of the loops, so this costs nothing to preserve.

The toggle means "stop the automatic spend", not "seal the feature". An operator who has switched a
job off needs a way to test that it works before switching it back on, and taking that away would
make the toggle something people hesitate to use.

The panel says so where the toggle is, so the scope is not a surprise.

`FlightSyncOptions.ManualSyncCooldown` still applies to manual runs, unchanged.

---

## 6. Admin panel

The `/health` page gains a **Jobs** section, above Data sources:

- One row per job: display name, effective state, last outcome and run time.
- A toggle, disabled when `canToggle` is false, with the config key named as the reason.
- "Switched off by <admin> at <time>" and the reason, when it is off.
- Switching off asks for an optional reason before sending.

A switched-off job renders amber, not red: it is a deliberate state, not a fault. This follows the
rule the data-source inventory already sets, where `NotConfigured` is gray rather than red because a
deliberate choice must not read as a failure.

---

## 7. Testing

**The loop restructure is the part that needs the most care**, because its failure mode is silent:
a bug means syncing quietly stops, or quietly keeps spending. It gets the most tests.

- A loop that is switched off completes ticks, does no work, makes no AeroAPI call, and marks
  `SwitchedOff` — asserted against a fake clock, not by waiting.
- A loop switched back on resumes on the next tick without a restart.
- The config ceiling still terminates the service, and the heartbeat still reads `NeverRun`.
- `effective` across all four `config × switch` combinations.
- The cache expires: a toggle takes effect within one window.

**API**

- `PUT` returns `409` when config has the job off, and does not write a switch row.
- `PUT` writes exactly one audit row, carrying the reason.
- A non-admin is refused.
- `GET` reports `canToggle: false` when config is off.

**Regression**

- Manual sync succeeds while the scheduled loop is switched off. This is a deliberate product
  decision and the only thing protecting it from a later "tidy-up" is a test that says so.
- `AdminAuditInterceptorTests` gains the `JobSwitch` exclusion, beside the existing ones.

---

## 8. Delivery

1. **Backend** — table, switch service, loop restructure, heartbeat states, both endpoints.
2. **Admin panel** — the Jobs section, after the backend merges and the client is regenerated.

Stacked in that order; the panel cannot compile against an unregenerated client.

---

## Risks

**Restructuring a live loop is the risk in this spec.** Both services currently prove they are off
by not existing. After this they prove it by deciding, every tick, not to act — and a wrong decision
is invisible until someone reads a bill or notices stale flight data. The tests above are written
against that failure mode specifically, and the heartbeat's new `SwitchedOff` state exists so the
health page can show the decision being made rather than leaving it implicit.

**A second risk is scope creep into a general feature-flag system.** This is two booleans with a
hard config ceiling. It is not a flag framework, and should not grow percentage rollouts, per-user
targeting, or flags for things that are not these two jobs.

---

## Open questions

None. Decisions taken in discussion and recorded above: config is a hard ceiling; the switch
persists; manual triggers keep working; a switched-off job reads amber rather than red.
