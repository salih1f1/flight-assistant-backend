# Auto-Verification of Reference Data — Design Spec

**Date:** 2026-09-14
**Status:** Approved in discussion, pending review of this document
**Issue:** #342
**Supersedes:** the import/export verification rules in `2026-05-10-verified-refdata-design.md` (see §7)

---

## Overview

Today a row is verified only when an admin clicks Verify. Nothing is verified by seeding or import, so no rows are verified, and exports (verified rows only) are nearly empty.

This design adds:

1. Checks against outside sources for **airports, airlines and countries**, started by an admin (one or many rows) or automatically when a tracked flight uses an unverified or stale row.
2. A database-backed queue and a background job that does the checking, so nothing slows down a request.
3. A "Needs attention" flow when a check fails: the admin sees the differences and decides.
4. Imports that verify rows and keep verification details, so data moves cleanly between test, stage and prod.

---

## Goals

- Verify reference data without checking rows one at a time.
- A row is auto-verified only when **every checked column exists in the source and matches**.
- Record **how** each row was verified (`VerificationSource`), not only by whom.
- Spend AeroAPI money only when an admin or a tracked flight asks for it, and never past the monthly budget.
- Keep today's manual Verify.

## Out of scope

- Outside sources for alliances, lounges, lounge providers, loyalty programs and loyalty program kinds. No suitable source is known; these stay manual.
- Checking `Airline.AllianceCode` and `Country.IsEU` / `IsSchengen` / `IsEEA`. The admin fills these by hand.
- Azure Service Bus or any external job system (see §4.5).
- A hard stop for all AeroAPI spend (#303). This design only keeps its own calls inside the budget.
- Automatically overwriting our data with source values.

---

## 1. Three ways to verify

| Way | Who starts it | What happens |
|---|---|---|
| **Manual** | Admin, on one row | Today's flow: the admin reads the row and clicks Verify. Recorded as `manual`. |
| **Admin-started check** | Admin, on one or many selected rows | The admin chooses **Manually** (review each row and click Verify) or **Against sources** (pick sources that fit the entity, see the cost, start). Source checks go to the queue. |
| **Automatic re-check** | A tracked flight | When a flight is tracked (`TrackFlightCommand`, used by both `POST /flights` and `POST /admin/travelers/{id}/flights`), its airline, both airports and their countries are queued if unverified or verified more than `StaleAfterDays` ago. |

---

## 2. Data model

### 2.1 New columns on all 8 verifiable entities

Added to `IVerifiable`, so `AdminAuditInterceptor`'s Verify detection (modified properties equal the `IVerifiable` set) includes them without a name list.

| Column | Type | Meaning |
|---|---|---|
| `verification_source` | `varchar(100)` null | `manual`, `import`, `ourairports`, `aeroapi`, `restcountries`; several joined with `+` (e.g. `ourairports+aeroapi`). Null when unverified. |
| `verification_issue` | `varchar(500)` null | Short summary when a check failed, e.g. `Name, City differ (ourairports)`. Null otherwise. |

Existing `IsVerified`, `VerifiedAt`, `VerifiedBy` stay. `VerifiedBy` is the admin's id, or `system` when the job verified the row.

**States a row can be in**

| State | `IsVerified` | `VerificationIssue` |
|---|---|---|
| Unverified | false | null |
| Verified | true | null |
| Needs attention | false | not null |

`SourceType` (`seed` / `admin`) is unchanged: it records where the row came from, not how it was confirmed.

### 2.2 Domain methods

```csharp
void Verify(string verifiedBy, string source);   // sets IsVerified, VerifiedAt, VerifiedBy, VerificationSource; clears VerificationIssue
void Unverify();                                  // clears all five
void MarkVerificationFailed(string issue);        // IsVerified = false, VerifiedAt/By/Source = null, VerificationIssue = issue
```

### 2.3 New table `verification_requests`

| Column | Type | Notes |
|---|---|---|
| `id` | `bigint` identity | |
| `entity_type` | `varchar(50)` | `Airport`, `Airline`, `Country` |
| `entity_key` | `varchar(50)` | Stored as a string: numeric id for airports/airlines, code for countries |
| `sources` | `varchar(100)` | e.g. `ourairports,aeroapi` |
| `trigger` | `varchar(20)` | `admin` or `flight` |
| `status` | `varchar(20)` | `Pending`, `Running`, `Verified`, `Mismatch`, `NotFound`, `Error` |
| `attempts` | `int` | |
| `next_attempt_utc` | `timestamptz` null | For retry backoff |
| `differences` | `jsonb` null | `[{ "column", "ours", "source", "sourceName" }]` |
| `source_values` | `jsonb` null | What each source returned, keyed by source name |
| `error` | `varchar(1000)` null | Last error for `Error` |
| `estimated_cost_usd` | `numeric(10,4)` | 0 for free sources |
| `requested_by` | `varchar(255)` | Admin id or `system` |
| `requested_utc`, `started_utc`, `completed_utc` | `timestamptz` | |

**Index:** unique partial index on `(entity_type, entity_key)` where `status in ('Pending','Running')`: at most one open check per entity.

---

## 3. What is checked

| Entity | Checked columns | Not checked | Sources |
|---|---|---|---|
| Airport | Iata, Icao, Name, City, CountryCode, Latitude, Longitude, ElevationFt | — | `ourairports`, `aeroapi` |
| Airline | Iata, Icao, Name, CountryCode | AllianceCode | `aeroapi` |
| Country | Code, Name, Alpha3, NumericCode, Region, Subregion | IsEU, IsSchengen, IsEEA | `restcountries` |

If group B from the field audit (e.g. Alpha3, NumericCode, Subregion, ElevationFt) is removed later, those columns simply drop out of this table.

OpenFlights is deliberately not a source: it has been stale since ~2017 and the airline seed came from it, so matching it proves nothing.

### 3.1 Matching rules

- **Codes** (IATA, ICAO, country codes, Alpha3, NumericCode): exact, after trimming and upper-casing.
- **Text** (Name, City, Region, Subregion): equal after normalisation — Unicode decomposition with accents removed, case-folded, punctuation removed, whitespace collapsed. `İstanbul Airport` = `Istanbul Airport`.
- **Latitude / Longitude:** within 0.01°.
- **ElevationFt:** within 50 ft.
- **Empty values:** empty on both sides is a match; empty on one side only is a mismatch.
- **Several sources:** the row is verified only if **every** selected source matches every checked column.
- **Not found:** if the row is missing from **at least one** selected source, the request is `NotFound`, treated like a mismatch for the row.

Tolerances are constants in the comparer, not configuration.

---

## 4. Backend flow

### 4.1 Queuing checks

**Admin-started**

```
GET  /admin/verification-sources?entityType=Airport
     → [{ "name": "ourairports", "costPerRowUsd": 0 }, { "name": "aeroapi", "costPerRowUsd": 0.015 }]

POST /admin/verification-requests
     { "entityType": "Airport", "keys": ["123", "456"], "sources": ["ourairports"] }
     → 202 { "queued": 1, "skipped": 1 }       // skipped = already Pending/Running
     → 400 when a source does not apply to the entity type, or keys is empty
```

**Flight-started**

After `TrackFlightCommand` saves, a domain event (dispatched post-commit by `DomainEventDispatchInterceptor`) is handled by a handler that:

1. Loads the airline, departure and arrival airports, and the countries of all three.
2. For each: queues a check with the default source if `IsVerified = false` or `VerifiedAt < now − StaleAfterDays`.
3. Skips rows with a `VerificationIssue` (waiting on an admin) and rows with an open request.
4. Catches and logs any failure. Tracking never fails because of verification.

Default sources: airport → `ourairports`, airline → `aeroapi`, country → `restcountries`.

### 4.2 The job — `ReferenceVerificationBackgroundService`

Follows `FlightStatusSyncBackgroundService`: polling loop, scoped services per cycle, heartbeat reported to the admin health report.

Each cycle:

1. Reset requests stuck in `Running` longer than 10 minutes to `Pending`.
2. Claim up to `BatchSize` requests: `status = 'Pending' and (next_attempt_utc is null or next_attempt_utc <= now)`, ordered by trigger (`flight` first), then `requested_utc`, using `FOR UPDATE SKIP LOCKED`; set `Running`.
3. For each request: load the entity, fetch from each source, compare, then:

| Outcome | Entity | Request |
|---|---|---|
| All sources match | `Verify("system", sources joined by +)` | `Verified` |
| Any mismatch | `MarkVerificationFailed(summary)` | `Mismatch`, `differences`, `source_values` |
| Not found | `MarkVerificationFailed("Not found in …")` | `NotFound`, `source_values` for the sources that did answer |
| Source error (timeout, 5xx, 429) | **Unchanged** | `attempts + 1`; back to `Pending` with backoff (1 min, 5 min); after `MaxAttempts` → `Error` |
| Entity deleted meanwhile | — | `Error` with `"Entity no longer exists"` |

The job's scope sets `IAdminContext` to a `system` actor, so its writes are audited like an admin's.

### 4.3 Sources — only when needed

`IVerificationSource` per source: `Name`, `CostPerRowUsd`, `SupportedEntityTypes`, and a fetch method returning a normalised record or not-found. No source runs on a timer.

- **`ourairports`** — no per-airport API, so the whole `https://ourairports.com/airports.csv` (~12 MB) is downloaded **only when a claimed request uses it**. Only needed columns are kept, keyed by IATA then ICAO. The parsed data is reused for at most 24 hours and dropped as soon as no open request uses `ourairports`.
- **`restcountries`** — `GET /v3.1/alpha?codes=TR,US,…&fields=cca2,name,cca3,ccn3,region,subregion` for the countries in the current batch only. Nothing is kept.
- **`aeroapi`** — `GET /operators/{icao|iata}` for airlines, `GET /airports/{icao|iata}` for airports, $0.015 each, one call per request. Calls use the existing AeroAPI `HttpClient` (throttle gate, resilience pipeline). Before each call the job compares this month's spend plus the call's cost to `MonthlyBudgetUsd`; if it would exceed it, the request stays `Pending`, the cycle stops calling AeroAPI, and the heartbeat reports **paused: AeroAPI budget**. An airport response also fills the existing timezone cache. Exact field mapping (notably the operator's country format) is confirmed in step 9.

### 4.4 Admin actions on a failed check

```
POST /admin/verification-requests/{id}/accept-source
     { "source": "ourairports" }
     → writes that source's stored values for the checked columns to the entity, Verify(adminId, source); 204
     → 409 if the request is not Mismatch, or any differing column's current value no longer equals the `ours` value stored in `differences`

POST /admin/verification-requests/{id}/retry
     → a new Pending request with the same sources; 202
     → 409 if the entity already has an open request

PATCH /admin/countries/{code}/verify          (#331)
```

"Keep ours and verify" uses the existing verify route, which now records `manual` and clears `VerificationIssue`. "Check again" after an edit is `POST /admin/verification-requests` for that one row.

`verification_requests` is added to `BrowsableTables` so it is visible and filterable in the panel's raw grid.

### 4.5 Why a database queue and not Service Bus

Considered Azure Service Bus (Basic ≈ $0 at this volume but no duplicate detection; Standard $10/month). Rejected for now:

- The table is needed anyway for status, differences, source values, history and dedupe.
- Saving a tracked flight and sending a message are not atomic without an outbox table, which is this queue.
- Volume is a few checks a day plus admin runs.
- Extra infrastructure, identity, local emulator and tests.

Queuing goes through one interface (`IVerificationQueue`), so a message signal can be added later without changing callers.

### 4.6 Configuration — `VerificationOptions`

| Option | Default |
|---|---|
| `PollInterval` | 30 s |
| `BatchSize` | 10 |
| `MaxAttempts` | 3 |
| `StaleAfterDays` | 180 |
| `DefaultSources` | Airport `ourairports`, Airline `aeroapi`, Country `restcountries` |

Validated on start like `AeroApiOptions`.

---

## 5. Panel

1. **Grid (`DataBrowserPage`)** — row selection on the 8 verifiable tables; **Verify selected (n)**; status badge (Verified with source / Needs attention / Unverified); the new columns are filterable. The "Verifying isn't available for Countries" notice is removed once #331 lands.
2. **Verify dialog** — **Manually**: the selected rows with their checked columns, a Verify button per row and **Verify all shown**. **Against sources** (airports, airlines, countries only): source checkboxes from `GET /admin/verification-sources`, live cost line (`12 × $0.015 = $0.18 · this month $2.10 of $5`), **Start**, then `Queued n · skipped m`.
3. **Detail page (`RecordPage`)** — verification panel with status, source, verified at and by. On Needs attention: differences table (`Column | Ours | <source>`) with **Take source values**, **Keep ours and verify**, **Edit** then **Check again**.
4. **Dashboard** — Needs attention card with counts per entity type, each linking to the grid filtered on `VerificationIssue` not empty. System health row for the job: heartbeat, pending count, paused state.
5. **History** — `verification_requests` in the sidebar as a raw table, with a **Retry** row action on `Error` rows.

---

## 6. Import and export

- **Export** stays verified-only and adds `VerifiedAt`, `VerifiedBy`, `VerificationSource` columns to each CSV.
- **Import** verifies every imported row:
  - If the CSV carries verification columns, they are kept as-is.
  - If not (older files), the row is verified with `VerifiedBy` = the importing admin and `VerificationSource = import`.
  - `VerificationIssue` is cleared.
- The audit row stays a single `Import` event.

## 7. Changes to earlier decisions

`2026-05-10-verified-refdata-design.md` said imported rows always arrive unverified and exports omit verification metadata. Both are reversed by §6: the export is the trusted set, and importing it into another environment carries that trust.

---

## 8. Testing

Every PR proves its tests by breaking the code, showing the failure, and restoring it.

**Backend**

- **Comparer:** accents, case, punctuation, whitespace; tolerance at exactly 0.01° / 50 ft and just past; both-empty vs one-empty; unchecked columns ignored; several sources where one disagrees.
- **Sources:** recorded HTTP fixtures (OurAirports CSV slice, restcountries JSON, AeroAPI operator/airport JSON); not found; missing column; timeout, 5xx, 429; OurAirports download reused within 24 h and dropped when no open request uses it; no call when no request needs the source.
- **Job (Postgres, as in `*PostgresTests`):** two workers never claim the same request; unique index merges duplicates; stuck `Running` reset; backoff and `MaxAttempts` → `Error` with the entity unchanged; budget pause leaves requests `Pending`; `system` audit rows.
- **Flight trigger:** queues unverified and stale (181 days); skips fresh (179 days), flagged and already-open; tracking succeeds when queuing throws.
- **Endpoints:** source validation; `queued`/`skipped` counts; accept-source 204 / 409 on changed entity; retry 409 on open request; verify by country code.
- **Import/export:** round trip keeps verification details; old CSV stamps `import`.

**Panel** (Vitest + React Testing Library)

- Selection and both dialog paths; cost line; manual-only for tables without sources; the three Needs-attention actions; dashboard card and health row.

---

## 9. Delivery

One issue → branch → PR per step; wait for review after each.

| # | Repo | Change |
|---|---|---|
| 1 | backend | #331: verify countries by code |
| 2 | backend | `VerificationSource`, `VerificationIssue`, migration, domain methods; manual verify records `manual` |
| 3 | backend | Import verifies and keeps verification details; export includes them |
| 4 | panel | Countries Verify, status badge, new columns |
| 5 | backend | `verification_requests`, comparer, job, `ourairports` and `restcountries` sources |
| 6 | backend | Admin endpoints: queue, sources, accept-source, retry; `verification_requests` browsable |
| 7 | panel | Row selection and Verify dialog |
| 8 | panel | Needs attention on detail page; dashboard card; health row |
| 9 | backend | `aeroapi` sources and budget check |
| 10 | backend | Flight-tracking trigger |

Steps 1–4 stand alone: countries become verifiable and imports work for moving data between environments. AeroAPI comes late so the free sources prove the approach first.
