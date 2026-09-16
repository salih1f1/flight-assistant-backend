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

**Error-cooldown index (#418).** A second partial index, `(entity_type, entity_key, completed_utc)`, supports the flight-trigger cooldown in §4.1. Filtered on `completed_utc is not null`, not `status = 'Error'`: the cooldown query's second half asks whether any row for the key completed *more recently*, of *any* status, which a `status = 'Error'`-filtered index cannot serve — see CLAUDE.md for the sequential-scan regression that filter caused, and why it must not come back.

**Canonical `entity_key` (controller ruling).** One stored form per entity, so the unique index above sees one entity as exactly one key: a two-letter, upper-cased code for `Country`; a positive `int` id in canonical decimal form (no sign, no leading zeros) for `Airport`/`Airline`. Surrounding whitespace is rejected outright, not trimmed. `EnqueueAsync`'s own duplicate-key pre-check compares these canonical keys.

**Concurrency.** Each row also carries PostgreSQL's own `xmin` system column as an optimistic-concurrency token — no extra column needed. A save against a stale token (typically a request that was reset as stuck and reclaimed by another cycle since it was loaded) throws; the job treats that as "claim lost": nothing is written and no attempt is counted for it. §4.2 covers how the claim and the processor use this token together.

**`EnqueueAsync`** takes a caller-supplied `estimatedCostUsd` — the sum of the requested sources' `CostPerRowUsd` from the source registry — which keeps a source that is not registered yet (e.g. `aeroapi` before step 9) enqueueable, and it saves the whole `DbContext` in one call. The flight-tracking trigger (step 10) enqueues *after* its own `TrackFlightCommand` save, not before — as built, this falls out for free, since the event it reacts to is dispatched by `DomainEventDispatchInterceptor` only post-commit (§4.1). A unique-index violation from a concurrent insert racing the pre-check is caught and counted as skipped, not surfaced as an error.

---

## 3. What is checked

| Entity | Checked columns | Not checked | Sources |
|---|---|---|---|
| Airport | Iata, Icao, Name, City, CountryCode, Latitude, Longitude, ElevationFt | — | `ourairports`, `aeroapi` |
| Airline | Iata, Icao, Name | AllianceCode, CountryCode | `aeroapi` |
| Country | Code, Name, Alpha3, NumericCode, Region, Subregion | IsEU, IsSchengen, IsEEA | `mledoze` |

**Airline `CountryCode` (#401, R1):** dropped from the checked columns once `aeroapi` — the airline source — turned out to report an operator's country as a *name* (`"United Kingdom"`), never a code; nothing in the app reads an airline's own `CountryCode` either. It stays checked on `Airport`, where every source reports a real two-letter code. The cost of leaving it unchecked is that an airline's country goes unverified, which it already was before this source existed. The column's own future (drop it, or add a name-to-code source) is tracked in #402, not decided here.

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
     → 200 [{ "name": "aeroapi", "costPerRowUsd": 0.015 }, { "name": "ourairports", "costPerRowUsd": 0 }]
     → 400 when entityType is missing, or not exactly Airport, Airline or Country
     // Only registered sources that support the type, ordered by name (Ordinal, so "aeroapi" sorts
     // before "ourairports"). Airline now returns [{ "name": "aeroapi", "costPerRowUsd": 0.015 }]
     // (#401, step 9) rather than [].

POST /admin/verification-requests
     { "entityType": "Airport", "keys": ["123", "456"], "sources": ["ourairports"] }
     → 202 { "queued": 1, "skipped": 1 }       // skipped = a duplicate key, or an entity with an open request
     → 400 when entityType is invalid; keys or sources is empty; keys has more than 1000 entries (a panel
       selection is at most one page of 200); a key is malformed for the entity type (named, e.g.
       "Keys[1]"); or a source is unregistered or does not support the entity type
     // Not in the response: each queued request stores estimated_cost_usd, the sum of the selected
     // sources' costPerRowUsd. Keys are not checked for existence: a missing entity completes as Error
     // ("Entity no longer exists") when the job claims it.
```

**Flight-started (#410, step 10 — as built; §9 notes where the original version of this section disagreed with it)**

`Flight.MarkTracked` raises a new domain event, `FlightTracked` (carrying only the flight, airline and both airport ids), on **every** track request — a new tracking link or a repeat — and `TrackFlightCommandHandler` now calls `SaveChangesAsync` unconditionally so the event dispatches even when the traveler already tracked the flight and nothing else changed. `DomainEventDispatchInterceptor` dispatches it post-commit, to `FlightTrackedVerificationEventHandler` (Application), which:

1. Loads the airline and both airports by id (one read apiece, never in a loop), then resolves the distinct country codes among all three (so the same country behind two or three rows costs one read, not several).
2. For each row: queues a check with the entity type's default source(s) when `IsVerified` is false, `VerifiedAt` is null, `VerifiedAt` is older than `StaleAfterDays`, `VerifiedAt` is in the **future** (an imported row keeps the file's own timestamp — §6 — and must not read as fresh forever), or `VerificationSource` is `import` (weaker evidence than an automated check, since a hand-built CSV is stamped the same way).
3. Skips a row outright when it carries a non-blank `VerificationIssue`, even if it also looks stale by rule 2 — an admin is already looking at it. The open-request skip is `EnqueueAsync`'s own behaviour (§2.3), not this handler's: it passes every key it decided on straight through.
4. Queues at most three `EnqueueAsync` calls — one per entity type, keys batched, not one call per row — and wraps the whole body in one `try`/`catch`: any failure is logged with the flight id and swallowed. Tracking must never fail because of verification: the dispatch is post-commit, not post-response (this runs synchronously inside the command's own `SaveChangesAsync`, before the endpoint builds its response), so an uncaught exception here would turn an already-committed tracking write into a 500 with nothing left to roll back.

Default sources: airport → `ourairports`, airline → `aeroapi`, country → `mledoze` (`Verification:DefaultSources`).

**Error cooldown (#418).** `EnqueueAsync` itself — not this handler — separately skips a key, for the flight trigger only, whose most recently *completed* request was `Error` within `Verification:ErrorCooldown` (default 6 h): a row a broken upstream just failed on stays skipped, counted in `skipped`, until the cooldown passes, instead of being re-queued (and re-billed) on every repeat track. `Verified`, `Mismatch` and `NotFound` never start this cooldown; the latter two are already excluded by rule 3 above. `0` disables it, and issues no extra query. The admin queue and retry routes (§4.4) always enqueue with trigger `admin`, which this check never gates — see CLAUDE.md for why that has to hold.

### 4.2 The job — `ReferenceVerificationBackgroundService`

Follows `FlightStatusSyncBackgroundService`: polling loop, scoped services per cycle, heartbeat reported to the admin health report. `Verification:Enabled` (default `true`, controller ruling, for parity with `FlightSync:Enabled`) gates the whole loop; disabled, the heartbeat stays `NeverRun` rather than reporting a broken job.

Each cycle:

1. **Reset stuck requests.** One set-based SQL statement returns any request stuck in `Running` for over 10 minutes to `Pending`. The reset counts an attempt for every row it touches, and completes a row as `Error` outright (`"Claim abandoned: no result recorded after N attempts"`, where N counts every attempt, source errors included) once that attempt reaches `MaxAttempts` — the same bound a source-error retry has, so a poison request or a crash loop that never records a result still runs out of attempts.
2. **Claim a batch.** One SQL statement: a CTE selects at most `BatchSize` ids (`status = 'Pending' and (next_attempt_utc is null or next_attempt_utc <= now)`, optionally excluding a caller-supplied id set, ordered by trigger (`flight` first), then `requested_utc`, then `id`) with `FOR UPDATE SKIP LOCKED` — a concurrent claimer skips a locked row instead of waiting and then claiming it a second time — and an `UPDATE … FROM` sets those rows `Running` with `started_utc = now`, returning each row's PostgreSQL `xmin` system column as its concurrency token. If this batch pauses any request for the AeroAPI budget (below), the cycle claims **one** further batch excluding those specific ids (#401 final fix G1) before moving on to release — see that section for why.
3. **Process each claimed request**, in its own DI scope carrying the system actor: re-load it by id and act on it only while it is still `Running` with the claim's own `started_utc` (an ownership check — a request reset as stuck and reclaimed since would otherwise be silently overwritten); then load the entity, fetch from each requested source in order, compare, then:

| Outcome | Entity | Request |
|---|---|---|
| All sources match | `Verify("system", sources joined by +)` | `Verified` |
| Any mismatch | `MarkVerificationFailed(summary)` | `Mismatch`, `differences`, `source_values` |
| Not found | `MarkVerificationFailed("Not found in …")` | `NotFound`, `source_values` for the sources that did answer |
| Source error (timeout, non-success status including 429, network error, body over the size cap, unparseable content, no usable rows) | **Unchanged** | `attempts + 1`; back to `Pending` with backoff; after `MaxAttempts` → `Error` |
| Entity deleted meanwhile | — | `Error` with `"Entity no longer exists"` |
| A requested source isn't registered (e.g. `aeroapi` before step 9), or doesn't support the entity type | **Unchanged** | `Error` straight away, naming the source — never retried, since retrying cannot change the answer |

**A row that completes `Error` is never flagged (#418).** Every `Error` path above leaves the entity's `IVerifiable` columns untouched — never `MarkVerificationFailed`, which only the `Mismatch`/`NotFound` rows above call. That includes step 1's own abandonment, above the table: a stuck `Running` claim exhausting `MaxAttempts` completes as `Error` exactly like the table's two processor paths, and starts the same flight-trigger cooldown in §4.1 just the same — a deploy that abandons a claim mid-check is one more resolved failure the cooldown treats normally, not a special case. Flagging it would conflate "could not check" with "is wrong": one upstream outage would flood needs-attention with rows that were never actually wrong, and, since a flagged row is skipped by the flight trigger's own stale-check rule, auto-re-verification would stay stopped for every one of them until an admin cleared each by hand. Instead, `Error` starts the flight-trigger cooldown in §4.1 — a time-boxed backoff, not a manual-intervention state. See CLAUDE.md for the rejected alternative and for why splitting permanent errors from transient exhaustion is left for later.

**Backoff (controller ruling):** 1 minute after the 1st failed attempt, 5 minutes after the 2nd and every later one. A source error is retryable but not necessarily transient — a reshaped upstream dataset fails the same way every time — which is why `MaxAttempts`, not the backoff itself, is what ends the retries.

**The `xmin` token also guards the save at the end of step 3.** If a request was reset as stuck and reclaimed by another cycle between the re-load and the save, the save fails on the stale token rather than silently overwriting the newer claim; this surfaces as "claim lost" — nothing is written, no attempt is counted.

**When even recording a failure fails** (the save that would record a source error itself throws), the request is left `Running` with no attempt counted at all; the stuck-request reset in step 1 is what eventually reclaims it and is the only place its attempt gets counted. This case is included in the cycle's reported failure count.

**Release failures don't lose the batch's outcome.** After every claimed request in the batch has been processed, the job releases the cached dataset of any source no open request still uses (§4.3), asking the queue for those source names in one query. That query is skipped only when no source holds anything (a dataset, a load in flight, or a cached failure): while a dataset is kept inside its idle window (§4.3), every poll, an idle one included, still runs it, and once nothing is held an idle poll costs only the reset and the claim. If that release itself fails, the heartbeat still reports the batch's real counts (marked failed), and the loop continues on its next poll — the batch was processed and saved before the release ran.

**Shutdown mid-batch returns the unfinished claims.** When the job's token is cancelled while a batch is being processed (a deploy or restart), the request in progress and every later claim go back to `Pending` with `started_utc` cleared and no attempt counted, instead of waiting out the 10-minute stuck reset, which would spend an attempt on each (and turn them straight into `Error` with `MaxAttempts = 1`). It is one set-based statement guarded by claim ownership (`status = 'Running'` and the claim's own `started_utc`), so a claim another worker has taken over, or an outcome already saved, is left alone. It runs on its own 5-second timeout, not the cancelled token, and is best effort: a failure is logged and the stuck reset remains the fallback.

**Unexpected exceptions store only their type, in every environment.** Unlike `DomainException.DiagnosticDetail`, gated to outside Production, an unexpected exception during processing is recorded as a fixed `"Unexpected error (<Type>); see logs"` regardless of environment — `error` carries no Production gating of its own, and an EF/Npgsql/argument exception's message can carry a host, port, table or constraint name, so only the exception's type name is safe to store unconditionally. The full exception, with the request id and the cycle's own correlation id, goes to the logs.

The job's scope sets `IAdminContext` to a `system` actor, so its writes to the entities it checks — `Verify` / `MarkVerificationFailed` — are audited like an admin's. **`verification_requests` itself is never audited:** it is its own history (every status change is already a row in it), and auditing it under the job's `system` actor would add an `Update` row per status change of every request in the queue.

**Health report.** `/admin/health` has a verification section (`AdminHealthVerificationDto`), shaped from one heartbeat snapshot like the flight-sync section: `Enabled`, `LastRunStartedUtc`, `LastRunCompletedUtc`, `LastOutcome` (`NeverRun`, `Running`, `Success`, `Paused` or `Failed`; `Failed` means the cycle itself failed, not a request in it), `RequestsProcessed`, `RequestsFailed`, `StuckAbandoned` and `FailuresUnrecorded` (#385; the last two from step 1's stuck-request reset and the "even recording a failure fails" case above) for the last completed cycle, `NextRunDueUtc`, `LastError`, `PendingCount` and `RequestsPaused`. All four original counts are set together by `MarkRunSucceeded` and by the batch-aware `MarkRunFailed` overload; the plain `MarkRunFailed(ex, next)` — a cycle that failed before it finished a batch — leaves the previous cycle's four counts as they were rather than zeroing them. Two fields degrade instead of failing the report. `PendingCount` (requests in `Pending`, due or waiting to retry) is `null` when the count query throws, meaning "cannot tell", not zero, and the rest of the report still renders. `LastError` follows the `DiagnosticDetail` convention: rendered outside Production only, always `null` in Production. With `Enabled` false the timestamps stay `null`: disabled, not broken.

**`Paused` is its own outcome (#401 task 2, R4/R5).** A cycle that worked through its batch but left some requests `Pending` because this month's AeroAPI budget is exhausted (see the `aeroapi` source below) reports `LastOutcome: "Paused"` and a non-zero `RequestsPaused`, via `VerificationJobHeartbeat.MarkRunPaused` — distinct from `"Success"` (nothing held back) and `"Failed"` (the cycle itself broke; a pause is the job declining to spend money it doesn't have, not a failure). `RequestsPaused` describes the last completed cycle exactly like `RequestsProcessed`, so a cycle that fails before finishing its own batch leaves it at whatever an earlier paused cycle last reported. A cycle that pauses some requests and then separately fails releasing source caches (§4.2's "release failures" case) still reports that batch's real `Paused` count, through the batch-aware `MarkRunFailed` overload.

**The replacement batch (#401 final fix G1).** A paused request returns to `Pending` with `requested_utc`/`id` untouched (R5 forbids nudging them to reorder it), so left alone it sits at the head of step 2's claim order again next cycle: a queue with `BatchSize` or more open `aeroapi`-naming requests would claim, pause and exclude exactly those every cycle, never reaching a country or airport request behind them, even though that work is free and has nothing to do with the budget. So once the initial batch has paused at least one request, the cycle claims **one** further batch excluding those specific ids and processes it the same way — sharing the same "budget already known Exhausted" flag, so it never asks the guard a second time either. Exactly one replacement batch, never a loop: a queue that is nothing but gated requests still ends the cycle cleanly (an empty replacement claim) rather than re-claiming forever. `RequestsProcessed` covers both batches when a replacement was claimed.

### 4.3 Sources — only when needed

`IVerificationSource` per source: `Name`, `CostPerRowUsd`, `SupportedEntityTypes`, and a fetch method returning a normalised record, not-found, or a source error. No source runs on a timer, and no source downloads at construction or DI-resolution time — only the first `LookupAsync` call that actually needs the dataset triggers a download.

**Shared lifecycle (both free sources).** `ourairports` and `mledoze` share one dataset-cache helper (download-once, reuse, release) and one download/parse pipeline, so this bookkeeping exists once rather than twice:
- A dataset loads only when a claimed request uses that source.
- It is reused for at most **24 hours** from when it was downloaded.
- It is dropped (released) once no open (`Pending`/`Running`) request uses that source **and** it has gone unused — no lookup answered from it — for `Verification:IdleRelease` (default 30 min; `0` releases immediately, today's pre-#378 behaviour): a one-row check soon after another then reuses the download instead of repeating it. The idle period never extends the 24-hour maximum age above — a dataset at or past it is dropped at the next release however recently it was used — and a cached failure is unaffected — it keeps only its own 60-second cooldown, never held for being recent. A load that finishes with no caller left waiting on it is stamped as last-used right then, so it still starts its idle clock even though nothing has asked for it since. The job checks after every cycle (§4.2).
- Concurrent callers needing the same stale-or-absent dataset share **one** download rather than each starting their own; a caller's own cancellation only cancels its own wait on the shared result, never the download itself or any other caller still waiting on it.
- A failed load is kept for a **60-second failure cooldown** (no longer than the first retry backoff), counted from when it failed: every lookup inside it gets the same source error with no new download, so a batch against a stalled or reshaped upstream downloads once, not once per request. The first lookup after the cooldown downloads again, and a release drops the failure. A load that ends cancelled is never kept.
- A dataset that parses but yields **zero** usable rows — an empty body, or every record failing to parse — is a source error, never "not found for everything": `NotFound` means the dataset loaded and has rows, just not this one, and a silently empty dataset must never look like that for every single lookup.
- Each download is capped: `ourairports` reads at most 64 MB and `mledoze` 8 MB. A declared `Content-Length` over the cap fails before the body is read; a body of unknown length is counted as it streams. Either is a source error.
- A source error stores a fixed category plus facts the code controls, never an upstream exception's message (a proxy error can name an internal address; a CSV or JSON error can quote raw records): `Timed out downloading the <source> dataset`, `<source> dataset download failed: HTTP <code>`, `Failed to download the <source> dataset (network error)`, `The <source> dataset is larger than the <N>-byte download limit`, `Could not parse the <source> dataset`, or `The <source> dataset had no usable rows`. The job prefixes the source name (`ourairports: …`). The source logs the full exception once per failed load.

- **`ourairports`** — no per-airport API, so the whole `https://ourairports.com/airports.csv` (~16 MB) is downloaded only when a claimed request needs it. Only the 8 checked columns (plus `type` and `score`, kept only to filter/dedupe while parsing) are kept in memory. **Lookup (controller ruling):** rows whose `type` is `closed` are dropped while parsing and never indexed; a lookup tries the entity's `Iata` against `iata_code` first, falling back to `Icao` against `icao_code` only when the IATA lookup is empty or finds no match; when several rows share a code, the one with the highest `score` wins. A malformed row (bad CSV data, a missing field, or a field count unlike the header's) is skipped instead of failing the dataset, with one warning per parse giving the count and the first few row numbers; a missing or renamed column still fails the whole parse.
- **`mledoze`** (replaces `restcountries`) — the free `https://raw.githubusercontent.com/mledoze/countries/master/countries.json` (~1.4 MB, ~250 records), downloaded and cached on the same lifecycle as `ourairports` above, rather than queried per-code per batch. **Why the change:** restcountries v3.1 (the source this spec originally named) was shut down; v5 needs an API key and has a monthly quota. mledoze/countries is the dataset restcountries itself was built from — still maintained, and needs neither. **Lookup (controller ruling):** by `cca2`, case-insensitively; `Country.Name` maps from `name.common`, never `name.official` (the latter is a longer, more formal string — e.g. common `"Turkey"` vs. official `"Republic of Türkiye"` — that would flag a mismatch on every well-formed row). A record missing `cca2` or `name.common` is skipped rather than failing the whole dataset, with one warning per parse giving the count and the first few indexes and `cca3` codes. **Licence:** ODbL-1.0, which requires attribution to mledoze/countries (and, transitively, the sources it was itself built from) wherever this data, or a work derived from it, is published or displayed.
- **`aeroapi`** (#401, step 9) — `GET /airports/{id}` for airports, `GET /operators/{id}` for airlines (AeroAPI's own term for an airline), $0.015 per lookup, one call per request. Calls use the existing `"AeroApi"` named `HttpClient` — the same throttle gate and resilience pipeline flight search, flight status and the usage report already share, so a verification lookup is authenticated, retried and throttled exactly like every other AeroAPI call, not a second, ungoverned path to it. **Matching (R2):** ICAO — AeroAPI's own identifier — is preferred, falling back to IATA only when ICAO is blank, never as a second call after the preferred one fails; both blank is `NotFound` without sending a request. **Errors (R3):** a 404 is `NotFound` (a real "AeroAPI has no row for this code" answer); every other failure (5xx, 429, timeout, network error, an unparseable 200) is a retryable `SourceError` with one of four fixed-category strings, never the upstream exception's or `DomainException`'s message. **Field mapping, confirmed against a live capture (fixtures: `FlightAssistant.Infrastructure.Tests/Fixtures/AeroApi/airport-sof.json`, `operator-baw.json`):** airport's `code_iata`/`code_icao`/`name`/`city`/`country_code` (already two-letter) map to `Iata`/`Icao`/`Name`/`City`/`CountryCode`, `latitude`/`longitude` to `Latitude`/`Longitude`, `elevation` to `ElevationFt` with no unit conversion (AeroAPI already reports feet); operator's `iata`/`icao`/`name` map to `Iata`/`Icao`/`Name` — its `country` is a name (`"United Kingdom"`), never a code, which is exactly why `CountryCode` came out of the airline's checked columns (R1, §3). A field AeroAPI's own response omits, or sends as JSON `null`, maps to `null` like any other missing value (final fix G2) — the same empty-values rule §3.1 already applies everywhere then flags it as a `Mismatch` against our populated row, surfacing AeroAPI's own gap as a needs-attention row rather than silently passing or silently dropping the column. **Timezone cache (R7):** a successful airport lookup already paid for the call, so it opportunistically fills the existing `AeroApiAirportTimezoneLookup` cache — through that lookup's own cache key, built from the response's own `code_iata` — with the response's `timezone`; it never reads that cache back to answer a check. **No dataset (R6):** every lookup is its own billed call, so `HasCachedData` is always false and there is nothing for the source's own `ReleaseCache` to drop. **Budget gate (R4/R5, task 2):** before fetching, a request naming `aeroapi` among its sources — and only such a request — asks `IProviderBudgetGuard` (the same guard flight search and status sync already use, #303; see CLAUDE.md) for this month's state; `SoftLimit` and `Normal` proceed (the soft limit exists to slow flight traffic, not an admin's explicitly requested check), and `Exhausted` returns the claim to `Pending` exactly as it was — `attempts`, `error` and `next_attempt_utc` untouched, only `status` and `started_utc` touched — rather than through the normal source-error path, and the cycle stops asking the guard for the rest of that cycle's `aeroapi`-naming requests (a request naming only free sources is never gated). The heartbeat reports this as `Paused`, not `Failed` or `Success` (see §4.2).

### 4.4 Admin actions on a failed check

```
POST /admin/verification-requests/{id}/accept-source
     { "source": "ourairports" }
     → 204: writes only that source's differing columns (the `differences` entries whose sourceName is
       `source`) — parsed into the column's own type — to the entity, then Verify(adminId, source), one
       save. A column that already matched keeps our own formatting. The request itself is unchanged:
       it stays Mismatch, as history, so accepting the same source again is a 409 only if that source had
       differences (those columns now hold its values); if it had none, it just verifies the entity again.
     → 400 if `source` is blank, is not one of the request's sources, or has no stored values
     → 404 if the request, or the entity, no longer exists
     → 409 if the request is not Mismatch; the entity has an open (Pending/Running) request — reference
       rows carry no concurrency token, so that newer check could save its own outcome over the accepted
       row; a differing column's current value no longer equals the `ours` value stored in `differences`,
       by the checked-column comparer's own rule, not raw string equality (`İstanbul` still equals
       `Istanbul`); a value can't be parsed, or would leave a required column empty; the merged row fails
       the entity's own admin-edit validator — the whole row, so a pre-existing invalid value in a column
       the source never touched also blocks the accept, naming that column; a new unique code
       (Airport/Airline Iata/Icao, Country Alpha3/NumericCode) already belongs to another row; or a new
       Airport/Airline CountryCode isn't one of our countries, which both tables reference by foreign key
       (a source can know XK). The last two are looked up before saving, so a genuinely concurrent write
       can still land between the lookup and the save and hit the database's own constraint as a 500, an
       accepted race. Country has no edit validator, so its formats and lengths go unchecked here.

POST /admin/verification-requests/{id}/retry
     → 202 { "queued": 1, "skipped": 0 }: a new Pending request, same entity type, key and sources,
       trigger admin, the caller as requested_by, cost from the registry. The retried request is unchanged.
     → 400 if one of its sources is no longer registered, or no longer supports the entity type
     → 404 if the request doesn't exist
     → 409 if the entity already has an open request — never the §4.1 error cooldown, which gates
       trigger `flight` only and a retry always enqueues as trigger `admin` (#418)

PATCH /admin/countries/{code}/verify          (#331)
```

Enqueue and retry each insert a `verification_requests` row, which is not audited (`NotAuditedTypes`);
`requested_by` is the only record of which admin asked. `accept-source`'s entity write is audited as an
admin `Update`.

"Keep ours and verify" uses the existing verify route, which now records `manual` and clears `VerificationIssue`. "Check again" after an edit is `POST /admin/verification-requests` for that one row.

`verification_requests` is browsable as `verification-requests`, exposing every mapped column except the `xmin` row-version shadow property; nothing is masked (`error` holds fixed categories, `requested_by` is an admin id).

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
| `IdleRelease` | 30 min (range 0–24 h); `0` releases a source's dataset as soon as no open request uses it |
| `StaleAfterDays` | 180 (range 1–3650) |
| `DefaultSources` | Airport `ourairports`, Airline `aeroapi`, Country `mledoze` |
| `ErrorCooldown` | 6 h (range 0–7 d); `0` disables it (#418) |

Validated on start like `AeroApiOptions`. **`Enabled`** (controller ruling, for parity with `FlightSync:Enabled`) gates the whole background service. Every source list — each `DefaultSources` entry, and every request's own `sources` column — follows the same one rule (`VerificationRequest.TryJoinSources`, shared by the domain and this options validation): at least one name; none blank or containing whitespace, `,` or `+`; duplicates dropped; the joined form fitting the `sources` column — so a configured default is always guaranteed to be a queueable request.

Since `aeroapi` registered (#401, step 9), all three `DefaultSources` entries now name a source the registry actually resolves — before that, the configured `aeroapi` default for `Airline` would have completed any request that used it straight to `Error` (`"Source 'aeroapi' is not registered."`), never retried. `DefaultSources` itself is still not read by the job, nor by the step-6 admin endpoints (whose `sources` is required and never defaulted) — it remains reserved for a caller that doesn't name sources explicitly.

### 4.7 Reading verification state for the panel (#385)

Landed ahead of step 8 so the panel work in §9 has something to read against: an entity's own check history, dashboard counts, and a grid filter for "needs attention" rows. None of these write anything, so none is audited (R6).

```
GET /admin/verification-requests?entityType=Airport&entityKey=123&limit=5
     → 200 [{ "id": 42, "entityType": "Airport", "entityKey": "123", "sources": ["ourairports"],
               "trigger": "Admin", "status": "Mismatch", "attempts": 1, "nextAttemptUtc": null,
               "differences": [{ "column": "Name", "ours": "…", "source": "…", "sourceName": "ourairports" }],
               "sourceValues": { "ourairports": { "Name": "…" } }, "error": null,
               "estimatedCostUsd": 0, "requestedBy": "admin-1", "requestedUtc": "…",
               "startedUtc": "…", "completedUtc": "…" }, …]
     → 400 when entityType isn't exactly Airport, Airline or Country; entityKey doesn't canonicalise
       for that type (same rule the queue uses, §2.3); or limit is outside 1-50
     // A "latest few", not a page (R1): newest first (requestedUtc descending, then id descending as
     // the tiebreaker), at most limit (default 5). No total, no PagedResult — the full history stays
     // browsable at GET /admin/data/verification-requests. differences and sourceValues come back
     // typed (R2, via VerificationJson), not as the stored comma-joined or JSON strings. An entity
     // with no checks yet is an empty 200, never a 404.

GET /admin/verification-needs-attention
     → 200 [{ "entityType": "Airport", "count": 3 }, { "entityType": "Airline", "count": 0 },
             { "entityType": "Country", "count": 1 }]
     // One entry per VerificationEntityType, always all three, in enum order, count 0 rather than an
     // omitted entry (R3). A row needs attention when it is unverified with a non-blank
     // VerificationIssue — the state only the job's mismatch and not-found paths leave; a row simply
     // never checked (IsVerified false, no issue) doesn't count. No parameters.

GET /admin/data/verification-requests?filter=VerificationIssue:*
     // Not a new route: {column}:* on a string column of any browsable table (R4) means "has a
     // value" — non-null and non-blank after trimming — alongside the filter's existing forms: exact
     // match for a non-string column, and column:>=value for "on or after" on a date column. Added for
     // this grid link (the dashboard card's drill-in, §5 item 4), but the rule lives in the
     // general-purpose BrowsableDataRepository.ApplyFilter, so it works on any exposed string column.
```

---

## 5. Panel

1. **Grid (`DataBrowserPage`)** — row selection on the 8 verifiable tables; **Verify selected (n)**; status badge (Verified with source / Needs attention / Unverified); the new columns are filterable. The "Verifying isn't available for Countries" notice is removed once #331 lands.
2. **Verify dialog** — **Manually**: the selected rows with their checked columns, a Verify button per row and **Verify all shown**. **Against sources** (airports, airlines, countries only): source checkboxes from `GET /admin/verification-sources`, live cost line (`12 × $0.015 = $0.18 · this month $2.10 of $5`), **Start**, then `Queued n · skipped m`.
3. **Detail page (`RecordPage`)** — verification panel with status, source, verified at and by. On Needs attention: differences table (`Column | Ours | <source>`) with **Take source values**, **Keep ours and verify**, **Edit** then **Check again**.
4. **Dashboard** — Needs attention card with counts per entity type, each linking to the grid filtered on `VerificationIssue:*` (§4.7: non-null and non-blank after trimming). System health row for the job: heartbeat, pending count, paused state.
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
- **Endpoints:** source validation; `queued`/`skipped` counts; accept-source 204 / 409 on changed entity; retry 409 on open request; verify by country code; queue and retry unaffected by the flight-trigger error cooldown (#418).
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
| 8 | panel | Needs attention on detail page; dashboard card; health row — the reads it needs (§4.7, §4.2) landed backend-side in #385 |
| 9 | backend | `aeroapi` sources and budget check |
| 10 | backend | Flight-tracking trigger |

**All ten steps are now landed.** #410 (step 10, the flight-tracking trigger) was the last; §4.1 above is the as-built shape.

**Landed after all ten steps (#418).** A source error left an entity looking exactly like one never checked, so the flight-tracking trigger re-queued it — and re-billed whatever source it used — on every repeat track against a broken upstream. `Verification:ErrorCooldown` (default 6 h) makes `EnqueueAsync` skip a flight-triggered key whose most recently completed request was `Error` inside the window (§2.3, §4.1); the admin queue and retry routes are unaffected, since both always enqueue with trigger `admin` (§4.4). See CLAUDE.md for why the entity itself is deliberately left unflagged, and for the index-filter defect (fix round 1) this uncovered.

Steps 1–4 stand alone: countries become verifiable and imports work for moving data between environments. AeroAPI comes late so the free sources prove the approach first.

**Carried into later steps**, noted during step 5's implementation and review:

- **Step 8, done ahead of the panel work (#385):** the health report's `StuckAbandoned` and `FailuresUnrecorded` counts (§4.2), the entity check-history read (`GET /admin/verification-requests`, §4.7), and the needs-attention counts (`GET /admin/verification-needs-attention`, §4.7) all landed backend-side so step 8 has something to read against. §5 item 5 (History) is folded into step 8 too, not a step of its own: `verification_requests`' sidebar table is served entirely by the existing data browser (A4) plus the `{column}:*` "has a value" filter (§4.7), so it needed no dedicated backend work beyond what's listed here. Still open: verifying a row through the queue doesn't refresh `/admin/stats` counts.
- **Step 9:** add a `ForAirline` lookup-key factory alongside `ForAirport`/`ForCountry`. Also: `started_utc` is stamped once per claimed *batch*, not per request; with several job instances and a throttled source (`aeroapi`, this step's own addition), a slow batch could push a claim past the 10-minute stuck cutoff before it finishes — revisit per-request stamping, or the cutoff length, once a throttled source exists to actually trigger it.
- **Step 10, and where it disagreed with the plan above:** the flight-tracking trigger handles an imported row's future `VerifiedAt` in its staleness check and treats `import` as weaker evidence than an automated check, both as planned. Two things this section originally assumed didn't hold once built: (1) it described the trigger as reacting to "a domain event," implying flight-tracking already raised one the way deletion and status-sync do — it didn't, so #410 added `FlightTracked` (Domain) rather than calling `IVerificationQueue` straight from `TrackFlightCommandHandler`, keeping the event/handler layering the rest of this design uses. (2) it described the handler as running "after `TrackFlightCommand` saves," implying every track request already reached `SaveChangesAsync` — it didn't: a repeat track that found the traveler already tracking the flight had nothing to write and skipped the save. #410 made the save (and the new event) unconditional, since a repeat track is exactly the case most likely to find a reference row gone stale since the last one.
