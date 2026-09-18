# Data-Source Inventory for the System Health Page — Design Spec

**Date:** 2026-09-18
**Status:** Approved in discussion, pending review of this document
**Issue:** #460

---

## Overview

`GET /admin/health` reports three checks (`database`, `aeroapi`, `apns`), two configured-or-not
booleans (Redis, App Insights), and three job panels (migrations, flight sync, verification). The
application actually depends on eleven outside things. Six of them appear nowhere on the page:

| Source | Used for | On the page today |
|---|---|---|
| AeroDataBox (RapidAPI) | route search fallback | nothing |
| GitHub API | CI runs on `/tests` | nothing |
| OurAirports CSV | airport verification | nothing |
| mledoze/countries | country verification | nothing |
| Azure App Service | hosting | nothing |
| Azure subscription spend | billing | nothing |

Three problems with what *is* shown:

1. **No "last checked".** Every check runs live on each request, so a timestamp would always read
   "just now". Adding the missing sources as live checks would put eleven outbound calls on every
   dashboard refresh, two of them metered per call.
2. **`aeroapi` "Healthy" is misleading.** `AeroApiHealthCheck` sends a `HEAD` to the base URL and
   calls any response healthy; the details column reads `Reachable — HTTP 401`. That is
   reachability, not "our key works" — and the row does not say which it means.
3. **Nothing says what a check verifies**, so a green row carries no information about what was
   actually proven.

This design adds a **data-source inventory**: one row per outside dependency, each carrying its
status, when it was last seen working, when it was last observed at all, a plain sentence saying
what that status is based on, a link to the source's own dashboard, and an operator-triggered
"Check now".

---

## Goals

- Every outside dependency appears on the page, and a new one cannot be added without appearing.
- A row says **what was checked**, not just green or red.
- "Last checked" is a real timestamp, not an artefact of when the page was loaded.
- Status reflects **real production calls**, not a synthetic ping.
- Observing health costs no extra outbound traffic and no money.
- An operator can deliberately test one source, and is told beforehand if that costs anything.
- "Never observed" is visibly different from "healthy".

## Out of scope

Each is its own issue, to be specced separately:

- A control panel for toggling `FlightSync:Enabled` / `Verification:Enabled` at runtime.
- Azure App Service health.
- Azure subscription budget and spend.
- The richer AeroAPI usage panel (portal link, per-operation success/failure, burn rate) and
  renaming the "Next AeroAPI slot" line.
- Historical trend or charting of source status. The table stores what is needed for it
  (§4) but this spec renders only current state.

---

## 1. The source catalog

A single registry enumerating every outside dependency. `IDataSourceCatalog` returns
`DataSourceDescriptor` records:

| Field | Meaning |
|---|---|
| `Key` | Stable id, used in URLs and as the table PK: `postgres`, `aeroapi`, `aerodatabox`, `github`, `ourairports`, `mledoze`, `redis`, `apns`, `appinsights` |
| `DisplayName` | "FlightAware AeroAPI" |
| `Kind` | `Database`, `FlightData`, `ReferenceData`, `Notifications`, `Telemetry`, `Infrastructure` — how the page groups rows |
| `WhatIsChecked` | One author-written sentence, rendered verbatim: "Signs in with the AeroAPI key and reads the free account-usage report." |
| `Criticality` | `Required` or `Optional` |
| `ExternalUrl` | Where the operator goes to check it themselves |
| `SupportsProbe` | Whether "Check now" is offered |
| `ProbeCostNote` | `null`, or a warning shown before probing: "Reachability only — every AeroDataBox call is billed." |

`WhatIsChecked` and `ExternalUrl` live here rather than in the health checks because they are facts
about the source, not about one particular check, and because a single registry is what makes the
completeness test in §8 possible.

`Criticality` decides whether missing credentials are a fault. GitHub, AeroDataBox and Redis are
`Optional`: the app is designed to start and run without them (`GitHubOptions.Token` and
`AeroDataBoxOptions.ApiKey` are both nullable by deliberate design, and route search falls back).
An optional source with no credentials is `NotConfigured` — gray, never red.

### 1.1 Registered sources

| Key | Kind | Criticality | External link |
|---|---|---|---|
| `postgres` | Database | Required | — |
| `aeroapi` | FlightData | Required | AeroAPI developer portal, API Usage tab |
| `aerodatabox` | FlightData | Optional | RapidAPI dashboard |
| `ourairports` | ReferenceData | Required | `ourairports.com/data/` |
| `mledoze` | ReferenceData | Required | the GitHub repository |
| `github` | Infrastructure | Optional | the repository's Actions tab |
| `redis` | Infrastructure | Optional | — |
| `apns` | Notifications | Required | Apple Developer console |
| `appinsights` | Telemetry | Optional | the Azure portal resource blade |

`ourairports` and `mledoze` are `Required` because verification cannot run without them; a failure
there is a real fault, not an absent optional feature.

---

## 2. Observing status

`IDataSourceObserver`, owned by Application, implemented in Infrastructure:

```csharp
void Record(string key, DataSourceOutcome outcome, DateTimeOffset at, string? detail);
```

```csharp
public enum DataSourceOutcome { Ok, Failed, Unauthorized, RateLimited, NotConfigured }
```

`Unauthorized` is split out from `Failed` deliberately: it is the state today's `aeroapi` row
reports as Healthy, and it needs a different remedy (rotate a key) from a network failure (wait).
`RateLimited` is likewise not a fault of ours — `GitHubCiRunProvider` already detects it.

### 2.1 Where it is called from

Every call site is an existing code path. **No new outbound traffic is introduced.**

| Source | Seam |
|---|---|
| `aeroapi`, `aerodatabox`, `github`, `ourairports`, `mledoze` | A `DataSourceObservingHandler : DelegatingHandler` on each named `HttpClient` |
| `postgres` | `DatabaseHealthCheck`, which already runs on every `/admin/health` request |
| `redis` | `RedisCacheService` — its `IDistributedCache` get and set already wrap every cache operation |
| `apns` | The APNs send path |

`appinsights` has no passive seam: telemetry leaves through the SDK's own pipeline, not through a
client this code owns. Its status comes from configuration presence at startup (§2.3) and from
Check now. The row says so — it claims only that a connection string resolves, which is all that
can honestly be observed from inside the process.

The two `SaveChangesInterceptor`s already registered (`AdminAuditInterceptor`,
`DomainEventDispatchInterceptor`) are deliberately **not** used for `postgres`. They observe
successful saves, so they can confirm the database is working but never see it fail —
`DatabaseHealthCheck` reports both.

A `DelegatingHandler` per named client is the cheapest correct seam: it observes every call through
that client, including calls in code not yet written, and it cannot be forgotten at a new call
site the way an explicit `Record` can. It maps the response to an outcome by status code
(`401`/`403` → `Unauthorized`, `429` → `RateLimited`, other non-success → `Failed`) and maps
`HttpRequestException` / `TaskCanceledException` to `Failed`.

`VerificationSourceDownloader.DownloadAsync` funnels both OurAirports and mledoze through one
method, so their handler sees every dataset fetch without per-source wiring.

### 2.2 What `detail` may contain

`detail` ends up in `data_source_status.last_error`, which admins read in every environment.
It follows the rule `VerificationSourceDownloader` already sets: **fixed categories plus facts this
code controls** — the source name, an HTTP status code, a size cap — and **never** an upstream
exception's message. A proxy or socket error can name an internal address; a CsvHelper or JSON
error can quote raw record content.

Anything derived from an exception beyond its category is gated to non-Production, matching
`AdminHealthReportBuilder`'s `includeDiagnostics` convention.

### 2.3 Seeding at startup

A hosted service runs once at startup and, for every `Optional` source whose credentials are
absent (`GitHubOptions.IsConfigured`, `AeroDataBoxOptions.IsConfigured`, the Redis connection
string, `ApplicationInsightsConnection.Resolve`), records `NotConfigured`.

Without this pass an unconfigured optional source would sit at `Unknown` forever — it is never
called, so nothing ever observes it — and the page could not tell "deliberately switched off" from
"we have no idea". The two mean different things to an operator and must not share a state.

---

## 3. Deriving a status

The **backend** derives status; the panel renders what it is given. One rule, in one place,
testable without a browser.

| Status | Rule |
|---|---|
| `Unknown` | Never observed and never probed |
| `NotConfigured` | Last outcome is `NotConfigured` |
| `Ok` | Last outcome is `Ok` |
| `Degraded` | Last outcome is `RateLimited`; or `Unauthorized`/`Failed` on an `Optional` source; or `Failed` on a `Required` source that also has an `Ok` within the last hour |
| `Unhealthy` | `Unauthorized` or `Failed` on a `Required` source with no `Ok` in the last hour |

`Unknown` is a first-class state, not a fake green. This follows the rule the existing Migrations
panel already sets: "can't tell" is a different claim from "nothing wrong", and must never render
as one.

`Unknown` is the normal state for every source immediately after a deploy, before any traffic. The
panel says so in words ("Not observed since deploy") and offers Check now beside it, so it reads as
awaiting information rather than as a fault.

A `Required` source that failed once but succeeded within the hour is `Degraded`, not `Unhealthy`:
a single transient failure among successful calls is worth showing and not worth alarming about.

---

## 4. Persistence

One row per source. Table `data_source_status`, PK `key`:

| Column | Type | Meaning |
|---|---|---|
| `key` | text, PK | Catalog key |
| `last_outcome` | text | Most recent outcome |
| `last_outcome_utc` | timestamptz | When it was observed — the "last checked" the page shows |
| `last_ok_utc` | timestamptz, null | Most recent `Ok`, for the Degraded/Unhealthy split |
| `last_error` | text, null | Sanitized per §2.2 |
| `consecutive_failures` | int | Reset to 0 on `Ok` |
| `last_probe_utc` | timestamptz, null | Most recent "Check now" |
| `last_probe_by` | text, null | The admin who pressed it |

Persisted rather than held in memory for two reasons: the page must survive a deploy (an in-memory
record blanks every row on each release, which is precisely when an operator is most likely to
look), and an App Service scaled past one instance would otherwise answer from whichever instance
happened to serve the request.

Rows are seeded from the catalog on startup so the table always has one row per source, and a
source removed from the catalog leaves an orphan row that the endpoint ignores.

### 4.1 Write throttling

A per-call write would be one database write per outbound HTTP call. Instead, an in-memory buffer
in front of the table, flushed by an `IHostedService`:

- **Immediately**, on an outcome that differs from the last persisted one, and on any non-`Ok`
  outcome. A newly-failing source is visible at once.
- **Coalesced otherwise**: a run of identical `Ok` outcomes writes at most once per source per five
  minutes.

A healthy, busy source therefore costs about twelve writes an hour. The buffer is flushed on
graceful shutdown; a hard kill loses at most one coalescing window of "still fine", which is the
only information the policy is willing to drop.

---

## 5. "Check now"

`POST /admin/health/sources/{key}/probe`

- Inherits `AdminOnly` from the `/admin` group.
- **Rate limited** to one probe per source per 30 seconds, so repeated clicking cannot spend money
  or hammer a third party.
- **Audited.** It is an operator-initiated call to an outside service; it goes in the audit log like
  any other write, recording who probed what and the outcome.
- Records its result through the same `IDataSourceObserver`, so a probe and a real call are stored
  the same way, and sets `last_probe_utc` / `last_probe_by`.
- Returns the refreshed row.
- `400` with `ProbeCostNote` when `SupportsProbe` is false.

### 5.1 What each probe does

The probe verifies **credentials wherever that is free, and reachability where it is not** — and the
row states which, so a green row is never read as more than it proves.

| Source | Probe | Proves | Cost |
|---|---|---|---|
| `postgres` | `CanConnectAsync` | connectivity | free |
| `aeroapi` | `GET /account/usage` | **the API key works** | **free** |
| `github` | `GET /rate_limit` | the token works | free, unmetered |
| `ourairports` | `HEAD airports.csv` | the dataset is served | free |
| `mledoze` | `HEAD countries.json` | the dataset is served | free |
| `redis` | set and read back a short-lived probe key | a cache round-trip works | free |
| `apns` | config present; sign a provider token locally, send nothing | the P8 key parses and signs | free |
| `aerodatabox` | `HEAD` the API host | reachability only | every real call is billed |
| `appinsights` | connection string resolves | configuration only | n/a |

The AeroAPI row is the significant upgrade. FlightAware bills `GET /account/usage` at zero — it is
already called 72 times in the current month at \$0.00, and it is the endpoint the existing
`GET /admin/usage/aeroapi` proxies. So the check moves from "the host answered" to "our key works"
at no cost, and the `Reachable — HTTP 401` line disappears.

AeroDataBox is the one source where a credential check would cost money on every press, so it does
not get one; its row says reachability only, in words, rather than implying more.

---

## 6. API

`GET /admin/health/sources` → `AdminDataSourceDto[]`.

Separate from `GET /admin/health` rather than nested inside it. `/admin/health` stays the fast
liveness report that runs checks live; this endpoint reads one small table and runs nothing. Keeping
them apart means the dashboard's summary stays cheap and this page's cost does not grow with the
number of sources.

```
key, displayName, kind, criticality, status, whatIsChecked, externalUrl,
lastOutcome, lastOutcomeUtc, lastOkUtc, lastError, consecutiveFailures,
canProbe, probeCostNote, lastProbeUtc, lastProbeBy
```

`status` is the derived value from §3. `lastError` is sanitized per §2.2 and Production-gated for
anything exception-derived.

---

## 7. Admin panel

A new route `/health` → `SystemHealthPage`, with a sidebar entry under a System group.

- The existing check table and the Migrations / Flight sync / Verification panels **move here
  unchanged** from `SystemHealthSection.tsx`.
- A new **Data sources** table, grouped by `Kind`: name · status · last OK · last checked · what's
  checked · ↗ link · Check now.
- `Unknown` rows render gray, reading "Not observed since deploy", with Check now beside them.
- A source with a `probeCostNote` shows it on the button's tooltip and in a confirmation before the
  call goes out.
- `SystemHealthSection` on the dashboard shrinks to a summary line — overall status, source count,
  how many are not `Ok` — linking to `/health`.

`Status` and `Panel` are lifted out of `SystemHealthSection.tsx` into `src/pages/health/` and
imported by both the page and the dashboard summary, rather than duplicated. `formatUtcMinute`,
`apiErrorMessage` and the existing status colour map are reused as they are.

---

## 8. Testing

Test-driven throughout.

**Unit (Application / Infrastructure)**

- Status derivation: every `outcome × criticality × lastOk` combination in §3, as a table test.
  Includes the two that motivate the design — `Unauthorized` on a `Required` source is `Unhealthy`,
  never `Ok`; a never-observed source is `Unknown`, never `Ok`.
- Write throttling: an outcome change writes immediately; a failure writes immediately; a run of
  identical `Ok`s writes once per window; the buffer flushes on shutdown.
- Error sanitization: an upstream exception message never reaches `last_error`.
- `DataSourceObservingHandler` status mapping, including `401` → `Unauthorized` and `429` →
  `RateLimited`.

**Integration (API)**

- `GET /admin/health/sources` shape, and that it returns one row per catalog entry including
  never-observed ones.
- Probe endpoint: rejects an unauthenticated caller, rejects a second probe inside the rate-limit
  window, writes an audit row, and `400`s for a source with `SupportsProbe` false.

**Catalog completeness**

- A test that enumerates the registered named `HttpClient`s and fails if any has no catalog entry.

  This is the test that earns its keep. Everything else verifies behaviour that is visible when it
  breaks; this one catches the failure mode that motivated the whole issue — a source being added
  and silently never appearing on the page. It is what stops source #12 from repeating the story of
  the six currently missing ones.

**Panel (Vitest)**

- `Unknown` renders gray, not green.
- `Unauthorized` renders as Degraded with its reason visible.
- A probe button for a costed source shows its note before calling.
- Rows group by kind; a `NotConfigured` optional source is gray, not red.

---

## 9. Delivery

Two pull requests, in order:

1. **Backend** — catalog, observer, handler, migration, derivation, both endpoints, tests.
2. **Admin panel** — after the backend merges and the OpenAPI client is regenerated: the `/health`
   page, the data-sources table, the dashboard summary.

Stacked in that order because the panel cannot compile against an unregenerated client. This is the
same ordering the verification-visibility work used.

---

## Open questions

None. Decisions taken in discussion and recorded above:

- Passive observation plus an operator-triggered probe, rather than a background prober on a timer —
  no scheduled outbound traffic, no scheduled spend, and status that reflects real production calls.
- Persisted rather than in-memory, so the page survives a deploy and a scale-out.
- A dedicated page with a dashboard summary, rather than an ever-growing dashboard section.
- AeroDataBox, GitHub and Redis are `Optional`; absent credentials are gray, not red.
