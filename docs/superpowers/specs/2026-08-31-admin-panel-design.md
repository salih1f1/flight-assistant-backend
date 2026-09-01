# Admin panel — operator console for data, health, AeroAPI spend and test results

**Status:** Approved — backend track A1 next
**Date:** 2026-08-31
**Issue:** #291
**Panel repo:** `salih1f1/flight-assistant-admin-panel` (separate)

## Why

Three concrete gaps, not a general wish for a dashboard.

**AeroAPI is metered on a $5/month personal tier and nothing counts the calls.** There
is no meter anywhere in the codebase. #281 (status sync polls flights up to 12 months
out that can never match), #282 (no caching on `/flights/search`, a public endpoint) and
#283 are all *"something is burning queries"* — and none of them can be confirmed or
closed without a usage number to look at. We are guessing at spend on a budget small
enough that one bad loop exhausts it.

**The panel already exists and has drifted.** `salih1f1/flight-assistant-admin-panel`
— React 18 + Vite 5 + TS + Tailwind v4 + shadcn/ui + MSAL/CIAM + React Query + TanStack
Table, with pages for travelers, flights, airlines, airports, countries, loyalty
programs and lounges — was last pushed 2026-03-28. Everything since (verified reference
data, CSV import/export, the AeroAPI migration, the `Airport.Timezone` removal) has no
UI, and its hand-written `src/types/index.ts` no longer matches the API. This is a
catch-up project, not a greenfield one.

**Only 7 of 18 tables are reachable.** Device tokens, traveler-flights, flight statuses
and events, alliances, lounge providers, loyalty tiers and kinds, memberships, provider
links and seed versions have no admin surface at all.

## Decisions

| Question | Decision |
|---|---|
| Test visibility | ~~**Read-only CI results**, read by the panel.~~ **Reversed 2026-09-01 (#298): GitHub Actions, not the panel.** CI runs on every PR and publishes a report. Duplicating it in the panel would have cost a token, a proxy and a cache to end up with a worse dashboard. Nothing executes from the panel either way. |
| AeroAPI usage store | ~~**Postgres table**, one row per outbound call.~~ **Reversed 2026-09-01 (#300): proxy AeroAPI's own `GET /account/usage`, cached 10 min.** FlightAware bills per *page*, not per call, and bills failed calls too — a locally counted, locally priced table got both wrong and could never be reconciled against the invoice. The provider's report is authoritative, and that endpoint is itself free. |
| Full DB coverage | **Hybrid** — generic read-only browser over an explicit whitelist, plus typed pages where you edit. |
| Audit trail | **`admin_audit_log`**, written in-transaction. |
| PII | **Redacted by default**, revealable on demand, every reveal audited. |
| Panel repo | **Evolve it**; replace hand-written types with codegen from `openapi.json`. |
| Sequencing | **Backend first**, panel once the spec is stable. |

## What already exists — do not rebuild

- `/admin` route group (`Program.cs`), gated by `RequireAuthorization("AdminOnly")`
  (authenticated + `roles: admin`) and `RequireCors("AdminPanel")`.
- Admin CRUD for travelers, flights, airlines, airports, loyalty and lounges;
  read-only countries and alliances — `FlightAssistant.API/Endpoints/Admin/`.
- `PATCH /admin/{entity}/{id}/verify` for 8 entity types via one generic mapper.
- CSV export (8 entities) and import (upsert, partial-success 207).
- Health checks `database`, `aeroapi`, `apns` (+ `redis` when configured), and
  `/health/live` + `/health/ready`. **Leave those two endpoints alone** — App Service
  probes depend on their exact behaviour.
- `openapi.json` already exported in CI.
- `PagedRequest` / `PagedResult<T>` / `PagedRequestValidator` — reuse for every new list.
- `TravelerContext` + `TravelerContextMiddleware` — the ambient-per-request pattern
  `AdminContext` copies.

---

## Backend track

### A1 — AeroAPI usage, proxied from the provider (revised 2026-09-01, issue #300)

**This section replaced a local meter** — an `aeroapi_calls` table, a recording
`DelegatingHandler`, an `AeroApi:Pricing` dictionary, a rollup and a retention prune —
built as PR #294 and closed unmerged. AeroAPI reports usage and cost itself, and the
local model was wrong in two ways no test caught: it priced **calls** when FlightAware
bills per **page** (`GET /airports`: 4 calls → 8 pages → $0.04, so every paginated search
was under-reported by its page multiplier — and multi-page search is exactly what #280
built), and it excluded **failed** calls, which are in fact billed (`/flights/search`: 1
failed call, $0.05). Nothing derived locally could ever be reconciled against the
invoice. None of `AeroApiCall`, `AeroApiUsageRecordingHandler`, `AeroApiEndpointTemplate`,
`IAeroApiUsageRecorder`, `AeroApiUsageRepository`, `AeroApiUsageRetentionService` or
`AeroApi:Pricing` exists — do not reintroduce them.

`GET /account/usage` (verified live) returns `total_calls`, `total_pages`, `total_cost`,
`total_discount_cost`, `total_successful_calls`, `total_failed_calls` and a
`resource_details` array of per-operation rows. `operation` arrives already normalised
(`GET /schedules/{date_start}/{date_end}`), so there is no client-side path templating
anywhere. **The endpoint itself is billed at $0.00**, so polling it costs nothing but a
throttle slot.

`AeroApiAccountUsageProvider` (Infrastructure) runs on the existing `"AeroApi"` named
client — inheriting `x-apikey`, the resilience pipeline and `AeroApiThrottleGate` — and
maps failures through the shared `AeroApiResponse.ReadOrThrowAsync`, so its status
mapping cannot drift from search and status. It is reached through
`IAeroApiAccountUsageProvider`, defined in Application.

`GetAeroApiUsageQuery(StartUtc?, EndUtc?)` → `GET /admin/usage/aeroapi` on the existing
`adminGroup`. Both bounds are optional and default to the current UTC month to date; the
default end is floored onto the same 10-minute grid as the cache TTL, which is what makes
the resolved range — reported, requested and used as the cache key — stable enough to
cache at all. AeroAPI serves at most **one year** of history, and the validator rejects an
earlier start itself: a 400 naming the real limit beats one relayed from upstream, and
costs no metered call. `ICacheService`, 10 minutes, matching FlightAware's own refresh
cadence — a shorter TTL buys no freshness and just spends throttle slots.

`AeroApi:MonthlyBudgetUsd` (default `5`) stays on `AeroApiOptions` for the gauge and
reaches Application through `IAeroApiBudgetOptions` — the `IFlightSearchHorizonOptions`
translation pattern, not a leak. `AeroApiThrottleGate` gains a read-only `NextSlotUtc`
(read under its existing reservation lock; reservation/release semantics untouched)
surfaced through `IAeroApiThrottleObserver` — *"next AeroAPI slot in 7s"* explains a slow
search instantly. Only the upstream payload is cached: budget and next slot are attached
fresh on every request.

### A2 — Admin health detail endpoint

`GET /admin/health`, returning what `/health/ready`'s plain-text output cannot: overall
status plus per-check name, status, description, duration and tags, running
`HealthCheckService.CheckHealthAsync()` with **no predicate** so the `external` checks
(aeroapi, apns) are included — this is the operator view, not a load-balancer probe.

Exception detail follows the existing `DiagnosticDetail` convention: rendered outside
Production only. Plus system facts — environment, build SHA, uptime, .NET version,
Redis and App Insights configured y/n, and applied vs. **pending EF migrations**
(`Database.GetPendingMigrationsAsync()`).

Also the last run and outcome of `FlightStatusSyncBackgroundService`, which currently has
no observable state; add a small singleton heartbeat it updates. That is also what makes
#283 visible.

`/health/live` and `/health/ready` are untouched.

### A3 — Admin audit log

Table `admin_audit_log`: `ActorId` (oid), `ActorName`, `ActionUtc`, `Action`
(`Create`|`Update`|`Delete`|`Verify`|`Import`|`Reveal`), `EntityType`, `EntityId`,
`Summary`, `Before` (jsonb), `After` (jsonb), `TraceId`.

Written by `AdminAuditInterceptor : SaveChangesInterceptor` reading the `ChangeTracker`
on **`SavingChanges`** — in the same transaction as the change. This is deliberately
unlike `DomainEventDispatchInterceptor`, which fires *after* commit: an audit row that
can roll back independently of the change it describes is worse than no audit row.

Actor comes from a scoped `AdminContext` populated by an endpoint filter on the `/admin`
group, mirroring `TravelerContext`/`TravelerContextMiddleware`. No `AdminContext` means
not an admin write means no row, so ordinary traveler traffic is unaffected.

`GET /admin/audit` — paged, filterable by entity type, actor and date.

### A4 — Generic table browser (read-only)

- `GET /admin/data/tables` → slug, display name, row count per browsable table.
- `GET /admin/data/{table}` → `PagedResult<Dictionary<string, object?>>` with `sort`
  and simple `filter`.

Backed by an **explicit `BrowsableTables` registry** mapping slug → CLR type → exposed
columns, each column flagged sensitive or not. Not open reflection over
`dbContext.Model`: a new entity or column must be classified deliberately, so nothing is
ever exposed by accident. `sort` and `filter` column names are validated against the
registry before reaching `EF.Property<object>()`.

Covers all 18 DbSets for reading. Strictly no writes on this path.

### A5 — PII redaction and audited reveal

Sensitive columns (`DeviceToken.Token`, `Traveler.Email`, membership numbers) are masked
**server-side in the projection** — the full value never leaves the API on a list request.

`POST /admin/data/{table}/{id}/reveal` with `{ column }` returns the single full value
and writes an `admin_audit_log` row with `Action = Reveal`. Add an `admin-reveal`
fixed-window policy to the existing `AddRateLimiter` block so reveal cannot be used to
walk the table.

### A6 — Typed endpoints where drill-down or writes are needed

A4 covers *seeing* everything, so this is deliberately short — only what needs a
relationship view or a write:

- **Traveler detail** — traveler with device tokens, tracked flights and memberships.
- **Flight detail** — flight with its `FlightStatus` and `FlightStatusEvent` history
  (the record of what the sync actually saw).
- **Device token delete** — remove a stale APNs token.

Alliances, lounge providers and loyalty kinds already have endpoints; they only lack UI.

### A7 — CI on pull requests — **done, and deliberately narrowed** (#298, PR #299)

Nothing ran on `pull_request`, so #290, #292 and #294 opened with zero checks and tests
only ran *after* merge, on the deploy pipeline. A separate `ci.yml` now builds, tests and
publishes a report on every PR — kept separate from the deploy workflow, which holds Azure
credentials and applies an EF migration bundle and must never be reachable from a PR,
including one from a fork. `trx_to_json.py` writes a `test-results.json` artifact and a
pass/fail table into the job summary.

**The panel gets no tests page. This reverses the original plan.**

The reasoning that put one here was "you asked to see tests, so the panel should show
them". That was wrong: the admin panel is for **operating live data**, while test results
are **developer-facing CI**, and GitHub Actions is already a better dashboard than
anything built here — run history, per-test detail, logs, re-run, PR integration, all
free. A panel page would be a worse copy of it behind a proxy that also needed a GitHub
token in Key Vault, a cache to survive rate limits, and two endpoints to maintain.

So: **no `GET /admin/ci/*` endpoints, and no B6.** Test visibility is the Actions tab, the
PR's own checks, and a build badge in the README. The `test-results.json` artifact stays —
it costs nothing and makes the summary readable — and if flaky-test tracking is ever
wanted, that calls for a purpose-built tool (BuildPulse, Currents), not a hand-rolled page.

### A8 — Publish the OpenAPI spec for codegen

CI produces `openapi.json` as an artifact, which is awkward to consume. Publish it
somewhere stable — a release asset, or committed at `spec/openapi.json` on `development`
— so the panel's codegen fetches it in one unauthenticated step.

---

## Panel track

Starts once the backend spec is stable.

**B0 — Foundation.** React 18 → 19, Vite 5 → 7. Add **orval** generating typed axios
clients and React Query hooks from `openapi.json` into `src/api/generated/`; it matches
the existing `src/api/*.ts` + `src/hooks/use*.ts` shape, so pages migrate incrementally
and `src/types/index.ts` dies one page at a time. Replace the placeholder Vite README.

Verify the roles claim end-to-end first: `useAdminAuth` reads `idTokenClaims.roles`, but
the API enforces the role on the **access token** (`RoleClaimType = "roles"`). Both come
from the same app-role assignment, but the API app registration must emit `roles` in the
access token. A panel that believes you are an admin while every request 403s is a
confusing first bug. `RequireAdmin` is a UX gate only; `AdminOnly` is the real one.

**B1 — Health & usage dashboard.** Replaces the six count tiles in `Dashboard.tsx`,
which fetch every list endpoint in full just to call `.length`. Budget gauge, cost and
pages this month, per-operation breakdown, failed-call count, next throttle slot,
per-check health, pending migrations, sync heartbeat.

**B2 — Data browser.** Table picker with row counts → paged, sortable generic grid on
the existing `DataTable`. Masked cells carry a reveal control that warns the access is logged.

**B3 — Reference data curation.** The largest UX gap: verify toggle across all 8 entity
types, verified/unverified filter, CSV export buttons, and an import flow that renders
the 207 partial-success `ImportResult` per-row rather than just "failed". Surface the
import ordering constraint (alliances → countries → airports → airlines), which today
exists only in endpoint summaries.

**B4** — alliances, lounge providers, loyalty kinds; traveler and flight drill-downs.
**B5** — audit log page plus a recent-activity strip on the dashboard.
**B6** — ~~tests page~~ **dropped.** See A7: GitHub Actions is already the better dashboard,
and duplicating it in the panel would cost a token, a proxy and a cache to end up worse.
**B7** — deploy to Azure Static Web Apps; point `AdminPanel:AllowedOrigin` at it.

## Verification

Per backend PR: `dotnet build --configuration Release && dotnet test`, plus

- **A1** — provider against a stub transport: the recorded `/account/usage` payload
  deserializes (`resource_details` included), and 401/403 → 500, 429 → 503, 400 → 400,
  unparseable → 502, asserting the shared `AeroApiResponse` mapping is wired up rather
  than reimplementing it. Handler: defaults to the current UTC month, a cache hit does
  not call the provider, a miss populates it. Validator: a start older than a year, and a
  start after its end, are both rejected. Integration in the style of
  `AdminImportEndpointTests`: 401 anonymous, 403 without the role, 200 with it, provider
  always substituted — **no live AeroAPI calls from tests.**
- **A2** — integration test in the style of `HealthEndpointTests`; assert `/health/live`
  and `/health/ready` are unchanged, and `/admin/health` is 401 anonymous, 403 without
  the role, 200 with it.
- **A3** — interceptor test asserting the audit row is written in the same transaction
  and rolls back with a failed save.
- **A4/A5** — a test that fails when an entity is added without classifying its columns;
  assert a list response never contains a full device token; assert reveal audits.
- **A7** — done. The converter was exercised against real TRX output and against a
  synthetic failing one, plus the no-results and wrong-file-count paths.

End to end: `docker compose up -d`, `dotnet run --project FlightAssistant.API`,
`npm run dev` in the panel, sign in as admin, then browse a table with a masked column,
verify a record and find it in the audit log, and read the health and usage dashboard.
Test results are checked on the PR itself, not in the panel.

## Out of scope

- **Running tests from the panel.** Rejected: the App Service host has no SDK or test
  assemblies, it would burn production CPU, and it is a remote-code-execution surface
  behind a single role claim.
- #281, #282 and #283 are AeroAPI cost bugs, not panel work — but A1 is what makes them
  measurable, so it is worth landing A1 first to see the effect of each fix.
- `AdminPanel:AllowedOrigin` is single-valued; multi-origin support is only needed if
  local dev must hit the deployed API.
