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

Added to `IVerifiable`, so `AdminAuditInterceptor`'s Verify detection (modified properties non-empty and all belong to the `IVerifiable` set, derived from the interface, no name list) includes them automatically.

| Column | Type | Meaning |
|---|---|---|
| `verification_source` | `varchar(100)` null | `manual`, `import`, `ourairports`, `aeroapi`, `mledoze`; several joined with `+` (e.g. `ourairports+aeroapi`). Null when unverified. |
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

**Argument guards (controller ruling, deferred from #355).** `Verify` rejects a blank `verifiedBy` or `source`, either one over its column size (255 / 100 characters), or a `verifiedAtUtc` that is not `DateTimeKind.Utc`; `MarkVerificationFailed` rejects a blank `issue` or one over 500 characters — an `ArgumentException` at the call site instead of a database error at `SaveChanges`. One shared rule set (`VerificationGuard`, Domain) is used by all five `IVerifiable` implementations (`SeedableEntity`, `Lounge`, `LoungeProvider`, `LoyaltyProgram`, `LoyaltyProgramKind`) so they cannot drift apart on them. The job's own mismatch/not-found summary is truncated to fit before it ever reaches this guard, so the job never hands the domain a value it would reject.

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

**Canonical `entity_key` (controller ruling).** One stored form per entity, so the unique index above sees one entity as exactly one key: a two-letter, upper-cased code for `Country`; a positive `int` id in canonical decimal form (no sign, no leading zeros) for `Airport`/`Airline`. Surrounding whitespace is rejected outright, not trimmed. `EnqueueAsync`'s own duplicate-key pre-check compares these canonical keys.

**Concurrency.** Each row also carries PostgreSQL's own `xmin` system column as an optimistic-concurrency token — no extra column needed. A save against a stale token (typically a request that was reset as stuck and reclaimed by another cycle since it was loaded) throws; the job treats that as "claim lost": nothing is written and no attempt is counted for it. §4.2 covers how the claim and the processor use this token together.

**`EnqueueAsync`** takes a caller-supplied `estimatedCostUsd` — the sum of the requested sources' `CostPerRowUsd` from the source registry — which keeps a source that is not registered yet (e.g. `aeroapi` before step 9) enqueueable, and it saves the whole `DbContext` in one call. The flight-tracking trigger (step 10) must therefore enqueue *after* its own `TrackFlightCommand` save, not before. A unique-index violation from a concurrent insert racing the pre-check is caught and counted as skipped, not surfaced as an error.

---

## 3. What is checked

| Entity | Checked columns | Not checked | Sources |
|---|---|---|---|
| Airport | Iata, Icao, Name, City, CountryCode, Latitude, Longitude, ElevationFt | — | `ourairports`, `aeroapi` |
| Airline | Iata, Icao, Name, CountryCode | AllianceCode | `aeroapi` |
| Country | Code, Name, Alpha3, NumericCode, Region, Subregion | IsEU, IsSchengen, IsEEA | `mledoze` |

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

Default sources: airport → `ourairports`, airline → `aeroapi`, country → `mledoze`.

### 4.2 The job — `ReferenceVerificationBackgroundService`

Follows `FlightStatusSyncBackgroundService`: polling loop, scoped services per cycle, heartbeat reported to the admin health report. `Verification:Enabled` (default `true`, controller ruling, for parity with `FlightSync:Enabled`) gates the whole loop; disabled, the heartbeat stays `NeverRun` rather than reporting a broken job.

Each cycle:

1. **Reset stuck requests.** One set-based SQL statement returns any request stuck in `Running` for over 10 minutes to `Pending`. The reset counts an attempt for every row it touches, and completes a row as `Error` outright, describing the claim as abandoned, once that attempt reaches `MaxAttempts` — the same bound a source-error retry has, so a poison request or a crash loop that never records a result still runs out of attempts.
2. **Claim a batch.** One SQL statement: a CTE selects at most `BatchSize` ids (`status = 'Pending' and (next_attempt_utc is null or next_attempt_utc <= now)`, ordered by trigger (`flight` first), then `requested_utc`, then `id`) with `FOR UPDATE SKIP LOCKED` — a concurrent claimer skips a locked row instead of waiting and then claiming it a second time — and an `UPDATE … FROM` sets those rows `Running` with `started_utc = now`, returning each row's PostgreSQL `xmin` system column as its concurrency token.
3. **Process each claimed request**, in its own DI scope carrying the system actor: re-load it by id and act on it only while it is still `Running` with the claim's own `started_utc` (an ownership check — a request reset as stuck and reclaimed since would otherwise be silently overwritten); then load the entity, fetch from each requested source in order, compare, then:

| Outcome | Entity | Request |
|---|---|---|
| All sources match | `Verify("system", sources joined by +)` | `Verified` |
| Any mismatch | `MarkVerificationFailed(summary)` | `Mismatch`, `differences`, `source_values` |
| Not found | `MarkVerificationFailed("Not found in …")` | `NotFound`, `source_values` for the sources that did answer |
| Source error (timeout, 5xx, 429, unparseable content) | **Unchanged** | `attempts + 1`; back to `Pending` with backoff; after `MaxAttempts` → `Error` |
| Entity deleted meanwhile | — | `Error` with `"Entity no longer exists"` |
| A requested source isn't registered (e.g. `aeroapi` before step 9), or doesn't support the entity type | **Unchanged** | `Error` straight away, naming the source — never retried, since retrying cannot change the answer |

**Backoff (controller ruling):** 1 minute after the 1st failed attempt, 5 minutes after the 2nd and every later one. A source error is retryable but not necessarily transient — a reshaped upstream dataset fails the same way every time — which is why `MaxAttempts`, not the backoff itself, is what ends the retries.

**The `xmin` token also guards the save at the end of step 3.** If a request was reset as stuck and reclaimed by another cycle between the re-load and the save, the save fails on the stale token rather than silently overwriting the newer claim; this surfaces as "claim lost" — nothing is written, no attempt is counted.

**When even recording a failure fails** (the save that would record a source error itself throws), the request is left `Running` with no attempt counted at all; the stuck-request reset in step 1 is what eventually reclaims it and is the only place its attempt gets counted. This case is included in the cycle's reported failure count.

**Release failures don't lose the batch's outcome.** After every claimed request in the batch has been processed, the job releases the cached dataset of any source no open request still uses (§4.3). If that release itself fails, the heartbeat still reports the batch's real counts (marked failed), and the loop continues on its next poll — the batch was processed and saved before the release ran.

**Unexpected exceptions store only their type, in every environment.** Unlike `DomainException.DiagnosticDetail`, gated to outside Production, an unexpected exception during processing is recorded as a fixed `"Unexpected error (<Type>); see logs"` regardless of environment — `error` carries no Production gating of its own, and an EF/Npgsql/argument exception's message can carry a host, port, table or constraint name, so only the exception's type name is safe to store unconditionally. The full exception, with the request id and the cycle's own correlation id, goes to the logs.

The job's scope sets `IAdminContext` to a `system` actor, so its writes to the entities it checks — `Verify` / `MarkVerificationFailed` — are audited like an admin's. **`verification_requests` itself is never audited:** it is its own history (every status change is already a row in it), and auditing it under the job's `system` actor would add an `Update` row per status change of every request in the queue.

### 4.3 Sources — only when needed

`IVerificationSource` per source: `Name`, `CostPerRowUsd`, `SupportedEntityTypes`, and a fetch method returning a normalised record, not-found, or a source error. No source runs on a timer, and no source downloads at construction or DI-resolution time — only the first `LookupAsync` call that actually needs the dataset triggers a download.

**Shared lifecycle (both free sources).** `ourairports` and `mledoze` share one dataset-cache helper (download-once, reuse, release) and one download/parse pipeline, so this bookkeeping exists once rather than twice:
- A dataset loads only when a claimed request uses that source.
- It is reused for at most **24 hours** from when it was downloaded.
- It is dropped (released) as soon as no open (`Pending`/`Running`) request uses that source; the job checks after every cycle (§4.2).
- Concurrent callers needing the same stale-or-absent dataset share **one** download rather than each starting their own; a caller's own cancellation only cancels its own wait on the shared result, never the download itself or any other caller still waiting on it.
- A failed load is evicted immediately, so the very next lookup retries rather than replaying the same failure for the rest of the 24-hour window.
- A dataset that parses but yields **zero** usable rows — an empty body, or every record failing to parse — is a source error, never "not found for everything": `NotFound` means the dataset loaded and has rows, just not this one, and a silently empty dataset must never look like that for every single lookup.

- **`ourairports`** — no per-airport API, so the whole `https://ourairports.com/airports.csv` (~16 MB) is downloaded only when a claimed request needs it. Only the 8 checked columns (plus `type` and `score`, kept only to filter/dedupe while parsing) are kept in memory. **Lookup (controller ruling):** rows whose `type` is `closed` are dropped while parsing and never indexed; a lookup tries the entity's `Iata` against `iata_code` first, falling back to `Icao` against `icao_code` only when the IATA lookup is empty or finds no match; when several rows share a code, the one with the highest `score` wins.
- **`mledoze`** (replaces `restcountries`) — the free `https://raw.githubusercontent.com/mledoze/countries/master/countries.json` (~1.4 MB, ~250 records), downloaded and cached on the same lifecycle as `ourairports` above, rather than queried per-code per batch. **Why the change:** restcountries v3.1 (the source this spec originally named) was shut down; v5 needs an API key and has a monthly quota. mledoze/countries is the dataset restcountries itself was built from — still maintained, and needs neither. **Lookup (controller ruling):** by `cca2`, case-insensitively; `Country.Name` maps from `name.common`, never `name.official` (the latter is a longer, more formal string — e.g. common `"Turkey"` vs. official `"Republic of Türkiye"` — that would flag a mismatch on every well-formed row). A single record missing `cca2` or `name.common` is skipped, logged as a warning, rather than failing the whole dataset. **Licence:** ODbL-1.0, which requires attribution to mledoze/countries (and, transitively, the sources it was itself built from) wherever this data, or a work derived from it, is published or displayed.
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
| `Enabled` | `true` |
| `PollInterval` | 30 s (range 1 s–1 h) |
| `BatchSize` | 10 (range 1–100) |
| `MaxAttempts` | 3 (range 1–10) |
| `StaleAfterDays` | 180 (range 1–3650) |
| `DefaultSources` | Airport `ourairports`, Airline `aeroapi`, Country `mledoze` |

Validated on start like `AeroApiOptions`. **`Enabled`** (controller ruling, for parity with `FlightSync:Enabled`) gates the whole background service. Every source list — each `DefaultSources` entry, and every request's own `sources` column — follows the same one rule (`VerificationRequest.TryJoinSources`, shared by the domain and this options validation): at least one name; none blank or containing whitespace, `,` or `+`; duplicates dropped; the joined form fitting the `sources` column — so a configured default is always guaranteed to be a queueable request.

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
- **Sources:** recorded HTTP fixtures (OurAirports CSV slice, mledoze JSON, AeroAPI operator/airport JSON); not found; missing column; timeout, 5xx, 429; OurAirports download reused within 24 h and dropped when no open request uses it; no call when no request needs the source.
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
| 5 | backend | `verification_requests`, comparer, job, `ourairports` and `mledoze` sources |
| 6 | backend | Admin endpoints: queue, sources, accept-source, retry; `verification_requests` browsable |
| 7 | panel | Row selection and Verify dialog |
| 8 | panel | Needs attention on detail page; dashboard card; health row |
| 9 | backend | `aeroapi` sources and budget check |
| 10 | backend | Flight-tracking trigger |

Steps 1–4 stand alone: countries become verifiable and imports work for moving data between environments. AeroAPI comes late so the free sources prove the approach first.

**Carried into later steps**, noted during step 5's implementation and review:

- **Step 6:** register `verification_requests` in `BrowsableTables` (it is `NotBrowsable` today, with a reason string naming this step, so `BrowsableTablesModelCompletenessTests` passes in the meantime).
- **Step 8:** show the health report's `StuckAbandoned` and `FailuresUnrecorded` counts (§4.2) alongside the heartbeat's processed/pending counts, which already ship. Also: verifying a row through the queue doesn't refresh `/admin/stats` counts.
- **Step 9:** add a `ForAirline` lookup-key factory alongside `ForAirport`/`ForCountry`. Also: `started_utc` is stamped once per claimed *batch*, not per request; with several job instances and a throttled source (`aeroapi`, this step's own addition), a slow batch could push a claim past the 10-minute stuck cutoff before it finishes — revisit per-request stamping, or the cutoff length, once a throttled source exists to actually trigger it.
- **Step 10:** the flight-tracking trigger must handle an imported row's future `VerifiedAt` in its staleness check, treat `import` as weaker evidence than an automated check when deciding whether to re-queue, and enqueue only *after* its own `TrackFlightCommand` save completes (§2.3's `EnqueueAsync` note).
