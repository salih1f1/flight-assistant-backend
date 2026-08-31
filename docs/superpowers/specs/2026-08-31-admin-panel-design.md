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
| Test visibility | **Read-only CI results.** CI publishes a report; the panel reads it. Nothing executes from the panel. |
| AeroAPI usage store | **Postgres table**, one row per outbound call. Durable, no Redis/App Insights dependency. |
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

### A1 — AeroAPI usage metering

`AeroApiCall : BaseEntity` in `Domain/Configuration/Entities/` (alongside `SeedVersion`):
`EndpointTemplate` (normalised — `/schedules/{range}`, `/flights/{ident}`,
`/airports/{id}`), `Method`, `StatusCode` (nullable: transport failures have none),
`DurationMs`, `Outcome` (`Success` | `Throttled` | `Failed`), `Caller` (`search` |
`status` | `timezone-lookup`), `CalledUtc`. Table `aeroapi_calls`.

Recorded by `AeroApiUsageRecordingHandler : DelegatingHandler` on the `"AeroApi"` named
client, registered **innermost — after `AeroApiThrottlingHandler`**. Both halves of that
placement are load-bearing:

- A row exists *iff* a request actually left the process. Quota is consumed by the
  transport, not by queueing, so a wait cancelled inside `AeroApiThrottleGate` must not
  be counted — `WaitForTurnAsync` already releases abandoned reservations for the same
  reason.
- Every **retry attempt** gets its own row, because every attempt is its own metered
  query. One row per logical request would under-report spend by up to
  `MaxRetryAttempts`×.

The handler lives on a singleton client, so it resolves `IServiceScopeFactory` per row
rather than capturing a scoped `DbContext`. Writes are best-effort — catch and log,
never fail the flight request. At ≤5 req/min a synchronous scoped write is fine; a
bounded `Channel<T>` plus drain service is the escape hatch if volume grows.

Unit costs live in `AeroApi:Pricing:{endpoint}` with `AeroApi:MonthlyBudgetUsd`
(default `5`) on `AeroApiOptions` — prices belong in config, FlightAware changes them.

Read side: `GetAeroApiUsageQuery(from, to)` → calls this UTC month vs. budget, estimated
spend, breakdown by endpoint and outcome, per-day series, 429/502 counts over 7 days.
Endpoint `GET /admin/usage/aeroapi`. Also expose a read-only `NextSlotUtc` on
`AeroApiThrottleGate` — *"next AeroAPI slot in 7s"* explains a slow search instantly.
Prune rows older than 90 days on a daily timer.

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

### A7 — CI test report

Change `dotnet test` to emit `--logger "trx;LogFileName=<project>.trx"`, convert the TRX
files to one compact JSON summary (suite, test, outcome, duration, failure message) and
upload it as a `test-results` artifact. Run on PRs too, not just pushes to
`development`, or the panel only ever shows merged history.

The panel must not hold a GitHub token, so the backend proxies:
`GET /admin/ci/runs` and `GET /admin/ci/runs/{id}/tests`. Token in Key Vault as
`GitHub:Token` (fine-grained, read-only, this repo) with `GitHub:Owner`/`Repo`/`Workflow`.
Cache through `ICacheService` for ~60s so panel polling never trips GitHub's rate limit.

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
which fetch every list endpoint in full just to call `.length`. Budget gauge, calls this
month, per-endpoint breakdown, per-day series, 429/502 counts, next throttle slot,
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
**B6** — tests page: CI runs, per-suite and per-test results, failure output, link out. Read-only.
**B7** — deploy to Azure Static Web Apps; point `AdminPanel:AllowedOrigin` at it.

## Verification

Per backend PR: `dotnet build --configuration Release && dotnet test`, plus

- **A1** — unit-test the handler against a stub transport: one row per attempt, no row
  for a cancelled throttle wait, correct outcome mapping for 429/502. Extend
  `AeroApiClientPipelineTests`, which already drives the real composed client on a
  `FakeTimeProvider`, to assert a retried request produces N rows — exactly the miscount
  this placement exists to prevent.
- **A2** — integration test in the style of `HealthEndpointTests`; assert `/health/live`
  and `/health/ready` are unchanged, and `/admin/health` is 401 anonymous, 403 without
  the role, 200 with it.
- **A3** — interceptor test asserting the audit row is written in the same transaction
  and rolls back with a failed save.
- **A4/A5** — a test that fails when an entity is added without classifying its columns;
  assert a list response never contains a full device token; assert reveal audits.
- **A7** — run the TRX converter over a real TRX and confirm the artifact parses.

End to end: `docker compose up -d`, `dotnet run --project FlightAssistant.API`,
`npm run dev` in the panel, sign in as admin, then browse a table with a masked column,
verify a record and find it in the audit log, read the health and usage dashboard, and
open the tests page.

## Out of scope

- **Running tests from the panel.** Rejected: the App Service host has no SDK or test
  assemblies, it would burn production CPU, and it is a remote-code-execution surface
  behind a single role claim.
- #281, #282 and #283 are AeroAPI cost bugs, not panel work — but A1 is what makes them
  measurable, so it is worth landing A1 first to see the effect of each fix.
- `AdminPanel:AllowedOrigin` is single-valued; multi-origin support is only needed if
  local dev must hit the deployed API.
