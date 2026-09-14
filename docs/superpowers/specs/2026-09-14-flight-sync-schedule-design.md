# Per-Flight Status Sync Schedule — Design Spec

**Date:** 2026-09-14
**Status:** Approved in discussion, pending review of this document
**Issue:** #349
**Fixes, once delivered:** #343, #344, #345

---

## Overview

Today the status sync works on all tracked flights at once:

- It picks up a flight only when its service date is between today and today + 3 days. A flight further out is never synced.
- Each batch syncs every flight in that list, then waits 10 minutes, or 2 minutes if **any** flight is within 4 hours of departure (#344).
- A flight joins the list about a day before AeroAPI can return it (#343) and stays after it has arrived (#345). Those calls return nothing new and are still billed.
- Nothing records when a flight was last synced or will be next. The admin panel's manual sync returns `{ updated: true|false }`, and the panel shows nothing.

This design:

1. Gives each tracked flight a **sync mode** and its own next-sync time, so each flight is synced as often as its mode needs and no more.
2. Checks **every tracked flight at least once a day**. Beyond AeroAPI's 2-day live-status window, the daily check uses `/schedules`.
3. Makes manual sync say **what happened**, and shows last sync, next sync and mode in the admin panel.

---

## Goals

- Every tracked flight is checked at least once a day, whatever its date.
- A flight is synced on its own schedule, not the busiest flight's.
- No AeroAPI call for a flight that can't be answered yet or has finished.
- An admin can see, per flight, when it was last synced, when it will be next, and why.
- A manual sync shows its result.

## Out of scope

- #347 (a local API run doubles the sync). Separate fix.
- #303 (a hard stop when the AeroAPI budget is spent).
- Telling travelers when a schedule check finds changed times or a dropped flight.
- Moving a flight to another service date when its schedule moves across midnight.
- A queue for manual syncs. Manual sync stays a direct call (the admin waits a few seconds for the result).
- Untracking flights from the admin panel.

---

## 1. Sync modes

A flight's mode comes from its tracking, its times and its status, at a given moment `now`:

| # | Mode | Rule (first match wins) | Synced every | AeroAPI call |
|---|---|---|---|---|
| 1 | *(none)* | No traveler tracks the flight | never | none |
| 2 | **Stopped** | `ActualArrivalUtc` ≤ now − 30 min; or `CancelledAtUtc` ≤ now − 30 min; or no `ActualArrivalUtc` and (`EstimatedArrivalUtc` ?? `ScheduledArrivalUtc`) ≤ now − 24 h | never | none |
| 3 | **Fast** | (`EstimatedDepartureUtc` ?? `ScheduledDepartureUtc`) − 4 h ≤ now | 2 min | `GET /flights/{ident}` |
| 4 | **Live** | `ScheduledDepartureUtc` − 2 days ≤ now | 10 min | `GET /flights/{ident}` |
| 5 | **Schedule** | otherwise (departure more than 2 days away) | 1 day | `GET /schedules/{date}/{date}` by flight number |

Notes:

- **Live** starts from the scheduled departure time, not the service date. AeroAPI's `end` is an exact time (`now + StatusWindowFutureDays`), so this is the moment the flight becomes answerable (#343). The 2 days is `AeroApi:StatusWindowFutureDays`, read the same way as today.
- **Fast** uses the estimated departure when there is one, so a delayed flight stays in fast polling until it actually leaves.
- **Stopped** keeps syncing for 30 minutes after arrival or cancellation, so the final status, gate and baggage claim are still recorded (#345). The 24-hour rule covers a flight that never reports an arrival; it replaces today's "service date ≥ today" lower bound.
- A **Diverted** flight is treated like any other: it stops 30 minutes after its actual arrival.

### 1.1 Next sync time

After a flight is synced at `now`, its next sync is set from its (recalculated) mode, **capped at the moment its mode would change**, so the stored mode is never out of date by more than one loop:

| Mode | `NextSyncDueUtc` |
|---|---|
| Fast | now + 2 min |
| Live | min(now + 10 min, start of Fast) |
| Schedule | min(now + 1 day, start of Live) |
| Stopped, none | null |

Fast and Live aren't capped at the stop time: the next sync after arrival finds the flight Stopped and makes no call (§2.2).

### 1.2 Where the rules live

One pure function in the Application layer, `FlightSyncPlanner.Plan(flight, isTracked, now, options)` → `(SyncMode?, NextSyncDueUtc?)`. The background service and the sync command both use it. It reads `StatusWindowFutureDays` through an Application-layer options interface, the way `IFlightSearchHorizonOptions` does.

---

## 2. Backend

### 2.1 New columns on `flights`

| Column | Type | Meaning |
|---|---|---|
| `last_synced_utc` | `timestamptz` null | When the flight was last checked against AeroAPI, by any call, whether or not anything changed. |
| `next_sync_due_utc` | `timestamptz` null | When the loop should sync it next. Null when the mode is Stopped or none. Indexed. |
| `sync_mode` | `varchar(16)` null | `Fast`, `Live`, `Schedule`, `Stopped`, or null when untracked. Stored as text so it reads well in the data grid. |
| `schedule_missing_since_utc` | `timestamptz` null | Set when a schedule check doesn't find the flight; cleared when a later check finds it. |
| `cancelled_at_utc` | `timestamptz` null | Set when a status update has code `CAN`; cleared when a later update has another code. Needed by the Stopped rule, because `Flight` doesn't store its current status. |

All five are added to the `flights` browsable table's column list, so the raw grid and CSV export show them.

**Migration backfill:** every tracked flight gets `next_sync_due_utc = now`, so each is planned on the first loop. Others stay null.

### 2.2 The loop

`FlightStatusSyncBackgroundService`:

1. Wakes every `FlightSync:LoopInterval` (1 minute).
2. Loads tracked flights with `NextSyncDueUtc ≤ now`, oldest due first (`GetDueTrackedFlightIdsAsync`, replacing `GetActiveTrackedFlightsAsync`).
3. Sends `SyncFlightStatusCommand(TravelerId: null, FlightId, Trigger: Scheduled)` for each, catching and counting failures as today.
4. The heartbeat is unchanged, except `NextRunDueUtc` becomes the next loop tick.

A run can take longer than a minute when many flights are due, because AeroAPI calls are spaced 12 seconds apart (`MinRequestInterval`). The next run starts when the current one ends, and flights are taken oldest due first, so none is skipped.

A flight that fails keeps its old `NextSyncDueUtc`, so it's retried on the next loop. That matches today, where a failed flight is retried in the next batch.

### 2.3 Sync command

`SyncFlightStatusCommand(Guid? TravelerId, int FlightId, SyncTrigger Trigger)`, where `SyncTrigger` is `Scheduled`, `Admin` or `Traveler`. It returns `FlightSyncResult(Outcome, LastSyncedUtc, NextSyncDueUtc, SyncMode, ScheduleMissingSinceUtc)`.

Steps:

1. Traveler check and 404 as today.
2. Plan the mode for `now`.
3. Decide the call:
   - Mode none: no call. Clear `SyncMode` and `NextSyncDueUtc`. Outcome `NotTracked`.
   - Stopped and `Trigger = Scheduled`: no call. Save the mode, set `NextSyncDueUtc` to null. Outcome `NoChange`.
   - Stopped and triggered by an admin or traveler: live status call (they asked for it; AeroAPI covers 10 days back).
   - Fast or Live: live status call, as today.
   - Schedule: schedule check (§2.4).
4. Set `LastSyncedUtc = now` for any call that reached AeroAPI, including one that returned nothing. Re-plan the mode (the call may have changed the times) and save the mode and `NextSyncDueUtc`.

On an AeroAPI failure (502/503) nothing is saved and the exception propagates, as today.

| Outcome | When |
|---|---|
| `StatusUpdated` | Live call recorded a new status event |
| `NoChange` | A call ran and found nothing new, or a Stopped flight was skipped by the loop |
| `NoDataYet` | Live call returned no row for the service date |
| `ScheduleUpdated` | Schedule check found changed scheduled times and saved them |
| `NotInSchedule` | Schedule check didn't find the flight |
| `NotTracked` | No traveler tracks the flight; no call |

`Flight.ApplyStatusUpdate` also sets or clears `CancelledAtUtc` (§2.1).

### 2.4 Schedule check

- Uses `IFlightSearchProvider` with `FlightSearchCriteria(null, null, ServiceDateUtc, FlightNumber)`, through the existing caching decorator.
- A match is a row with the same flight number, service date and departure airport IATA.
- **Found:** if its scheduled departure or arrival differs, `Flight.ApplyScheduleUpdate(departureUtc, arrivalUtc)` saves them (new domain method; sets `UpdatedUtc`) and the outcome is `ScheduleUpdated`, otherwise `NoChange`. Either way `ScheduleMissingSinceUtc` is cleared.
- **Not found:** `ScheduleMissingSinceUtc ??= now`. Outcome `NotInSchedule`. The flight stays tracked and keeps its daily check.
- The cache keeps future-date searches for 12 hours (`SearchCacheTtlFuture`). The daily check is 24 hours apart, so it normally misses the cache and makes a call. A manual sync within 12 hours of a check or search reuses the cached result and costs nothing.

### 2.5 Tracking and untracking

- `TrackFlightCommand` (used by `POST /flights` and `POST /admin/travelers/{id}/flights`): when the flight has no mode, plan it and set `NextSyncDueUtc = now`, so the loop syncs it within a minute.
- `UntrackFlightCommand`: when the last traveler stops tracking, clear `SyncMode` and `NextSyncDueUtc`. `LastSyncedUtc` is kept.

### 2.6 API

**`POST /admin/flights/{id}/sync`** (breaking change for the panel only) returns `AdminFlightSyncResultDto`:

```json
{
  "outcome": "NoDataYet",
  "lastSyncedUtc": "2026-09-14T20:31:05Z",
  "nextSyncDueUtc": "2026-09-14T20:41:05Z",
  "syncMode": "Live",
  "scheduleMissingSinceUtc": null
}
```

Responses: 200, 404 (unknown flight), 502/503 (AeroAPI unavailable, as today).

**`POST /flights/{flightId}/sync`** (public) keeps `{ updated }`, where `updated` is true for `StatusUpdated` and `ScheduleUpdated`.

**`AdminFlightDto` and `AdminFlightDetailDto`** add `LastSyncedUtc`, `NextSyncDueUtc`, `SyncMode`, `ScheduleMissingSinceUtc` and `CancelledAtUtc`.

### 2.7 Configuration

`FlightSync` section:

| Key | Default | Status |
|---|---|---|
| `Enabled` | `true` | existing |
| `LoopInterval` | `00:01:00` | new |
| `NearDepartureInterval` | `00:02:00` | existing, now the Fast interval |
| `Interval` | `00:10:00` | existing, now the Live interval |
| `ScheduleCheckInterval` | `1.00:00:00` | new |
| `FastWindowBeforeDeparture` | `04:00:00` | new (hardcoded 4 h today) |
| `StopAfterArrival` | `00:30:00` | new |
| `StopWithoutArrivalAfter` | `1.00:00:00` | new |

Bound to an options class validated on start, like `AeroApiOptions`.

---

## 3. Admin panel

### 3.1 Manual sync feedback

On the flights grid and on the flight detail page:

- While the sync runs, the Sync button is disabled and its icon spins. Only the row being synced spins.
- When it ends, a one-line result appears (`role="status"`). On the grid it names the flight, e.g. `TK1028 · 2026-09-17: AeroAPI has no live data for this flight yet. Next sync 20:41 UTC.`

| Outcome | Message |
|---|---|
| `StatusUpdated` | New status recorded. |
| `NoChange` | Checked, nothing changed. |
| `NoDataYet` | AeroAPI has no live data for this flight yet. |
| `ScheduleUpdated` | The scheduled times changed and were updated. |
| `NotInSchedule` | This flight is no longer in the airline's schedule. |
| `NotTracked` | No traveler tracks this flight, so it isn't synced. |

- "Next sync …" is added when `nextSyncDueUtc` is set.
- Errors show in red with `apiErrorMessage`, as elsewhere.
- On success the grid's raw tables and the flight's detail query are refreshed.

### 3.2 Flight detail page

A **Sync** section above "Current status":

- Mode, as a coloured label matching the grid (§3.3), or "Not synced (no traveler tracks this flight)".
- Last sync and next sync, in UTC.
- **Next sync overdue** (amber) when `nextSyncDueUtc` is more than 5 minutes ago, which means the background sync isn't running.
- **Not in schedule since …** (red) when `scheduleMissingSinceUtc` is set.
- The Sync button and its result line.

### 3.3 Grid row colours

On the flights grid only, each row gets a light background and a coloured left border from its `SyncMode`:

| State | Colour |
|---|---|
| `ScheduleMissingSinceUtc` set (overrides the rest) | red |
| Fast | green |
| Live | blue |
| Schedule | grey |
| Stopped, none | no colour |

A small legend sits above the grid. The `SyncMode` column is always visible text, so colour isn't the only signal.

---

## 4. Calls per flight

| Mode | Most calls per day |
|---|---|
| Fast | 720 (only for the hours between 4 h before departure and 30 min after arrival) |
| Live | 144 |
| Schedule | 1 |
| Stopped, none | 0 |

Compared with today:

- No flight is synced faster than its own mode (#344).
- A flight more than 2 days out costs one call a day instead of up to 144 wasted calls (#343).
- Nothing is called 30 minutes after arrival (#345).
- Flights more than 3 days out now cost one call a day, where today they cost nothing and were never checked.

---

## 5. Testing

**Backend**

- `FlightSyncPlanner`: each rule at its boundary, 1 minute either side (4 h before departure, 2 days before departure, 30 min after arrival or cancellation, 24 h without arrival); estimated departure moving Fast; untracked; every next-due cap in §1.1.
- `SyncFlightStatusCommandHandler`: each outcome; `LastSyncedUtc` set on an empty live response; loop skips a Stopped flight with no provider call; admin sync of a Stopped flight still calls; AeroAPI failure saves nothing; `CancelledAtUtc` set and cleared.
- Schedule check: times changed, unchanged, flight missing then found again (clears the flag), match requires the same departure airport.
- `FlightStatusSyncBackgroundService`: only due flights are synced; with one Fast and one Schedule flight over 10 minutes, the Schedule flight is synced once (#344).
- `GetDueTrackedFlightIdsAsync` (integration): tracked and due only, oldest due first.
- Track and untrack: first tracker plans the flight; last untrack clears the mode.
- Endpoints (integration): admin sync response shape; public sync still returns `{ updated }`.

**Panel**

- A message per outcome; the spinner on the synced row only; an error in red.
- Detail page: Sync section values, overdue and not-in-schedule warnings, Sync button refreshes the page.
- Grid: row colour per mode, red override, legend shown on flights only.

Each new test is shown failing against deliberately broken code before it's accepted.

---

## 6. Delivery

One issue and PR each, in order:

| # | Repo | Step |
|---|---|---|
| 1 | backend | Columns, `FlightSyncPlanner`, due-only loop, Fast / Live / Stopped, tracking and untracking, browsable columns. Schedule-mode flights make no call yet and are next due at the start of Live. Closes #343, #344, #345. |
| 2 | backend | Daily schedule check (§2.4), `ScheduleMissingSinceUtc`. |
| 3 | backend | Manual sync outcome and `AdminFlightSyncResultDto`; new fields on the admin flight DTOs. |
| 4 | panel | Sync feedback on the grid, Sync section on the detail page. Needs step 3 merged and deployed. |
| 5 | panel | Grid row colours and legend. |
