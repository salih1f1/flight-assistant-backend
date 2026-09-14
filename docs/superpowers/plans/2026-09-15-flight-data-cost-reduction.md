# Plan — flight-data cost reduction & multi-provider routing (#364)

Issue: #364 · Date: 2026-09-15 · Status: approved in discussion, not started

## Context

AeroAPI is the only flight-data provider, and the current usage pattern won't survive real users.
Your constraints: **≤ ~$100/month**, **50–500 travelers** in the first 6 months, **key events are
enough** (departed / landed / cancelled / diverted, plus gate changes and delays over 30 min), and
you're **open to routing each job to whichever provider is cheapest**.

### Where the money goes (AeroAPI list prices, per result set of 15 rows)

| Call | Price | Used by |
|---|---|---|
| `GET /flights/{ident}` | $0.005 | status sync |
| `GET /schedules/{start}/{end}` | **$0.020** | search, and the Schedule mode planned in #349 |
| `GET /airports/{id}` | $0.015 | timezone lookup for search (cached 30 days) |
| Push alert, create or delete | $0.000 | not used yet |
| Push alert, delivery | $0.020 | not used yet |

Failed calls are billed too. The Personal tier's hard limit is **10 result sets per minute**. Our
throttle sends one call every 12 s, so the whole process is capped at about 7,200 calls a day.

**Status sync (the biggest cost).** `FlightStatusSyncBackgroundService` polls every tracked flight
from its service date minus 3 days onward:

- The normal interval is 10 minutes. If *any* flight is near departure, every flight is polled every
  2 minutes.
- Polling continues after a flight lands.
- One flight tracked on its own costs about **740 calls, roughly $3.70**. When other flights keep
  the loop in fast mode, it costs up to about $14.
- About 430 of those calls land in the 3 days before AeroAPI can see the flight at all (#343).

**Search.** A search that misses the cache costs 1–4 `/schedules` pages plus sometimes one
`/airports` call, about **$0.02–0.10**.

**Cost leaks:**
- `POST /flights/{id}/sync` makes a live call on every request, with no rate limit or cooldown
  (`FlightEndpoints.cs:173-187`).
- Search allows anonymous users and is rate-limited by IP.
- In flight-number search, a failed timezone lookup isn't cached, so an origin that keeps failing
  costs one `/airports` call **per row** (`AeroApiFlightSearchProvider.cs:256-279`,
  `AeroApiAirportTimezoneLookup.cs:59-62`).
- Background polls retry up to twice, and each retry is billed.

### What $100 a month buys (≈ tracked flights per month, search excluded)

| Stage | Status cost per flight | Flights per $100 |
|---|---|---|
| Today | ~$3.70 | ~27 |
| Phase 1: planner (#349) plus tuned intervals | ~$0.90 | ~110 |
| Phase 2: AeroAPI push alerts plus a few targeted polls | ~$0.10 typical, $0.30 worst | ~350–1,000 |
| Phase 3: search moved to AeroDataBox | search becomes a flat $15–37.50 plan | — |

The alert figure assumes 2–4 deliveries per flight at $0.02 each (departure and arrival bundles,
plus gate or delay changes), plus 6–10 polls at $0.005. The Phase 2 spike confirms it.

---

## Target architecture: cheapest provider per job

| Job | Primary | Fallback |
|---|---|---|
| Route search (origin + destination + date) | **AeroDataBox FIDS**: departures from the origin for its local day, 2 × 12 h windows × 2 units ≈ **$0.003**, cached per (origin, local date) so every destination from that origin shares the cache | AeroAPI `/schedules` |
| Flight-number search, daily schedule check | **AeroDataBox** `flights/Number/{fn}/{date}` (2 units) | AeroAPI `/schedules` |
| Live status events | **AeroAPI push alerts** (webhook) | AeroAPI polling |
| Gate / terminal / baggage snapshots | AeroAPI `/flights/{ident}`, a few targeted polls | AeroDataBox `flights/Number` |
| Airport timezone | Not needed for AeroDataBox, which takes local-time windows. AeroAPI fallback keeps the cached lookup | — |

**Why AeroDataBox:**
- It's cheap: $15–37.50 a month for 15k–50k units, commercial use allowed from Pro.
- FIDS windows are in local time, which removes the timezone-shift problem entirely.
- We already had providers for it. They're in git at `21e55cf^`
  (`ExternalServices/FlightSearch/AeroDataBoxFlightSearchProvider.cs`,
  `FlightStatus/AeroDataBoxFlightStatusProvider.cs`).
- It was dropped earlier to consolidate cost before launch, not for data quality. With a $100
  budget that reason no longer applies.

**Ruled out:**
- Aviationstack: future search can't filter by route.
- Amadeus Self-Service: closed.
- Cirium and OAG: enterprise pricing.
- OpenSky: non-commercial use only.

Status stays on AeroAPI, which has the best live data. AeroDataBox status is only a fallback,
unless the Phase 3 spike shows its quality is comparable.

---

## Phase 1: stop the bleeding (AeroAPI only, small independent PRs)

Each item is its own issue, branch `feature/{issue}-…` and PR, done one at a time.

1. **Finish #356 / #349 (per-flight sync planner) with cheaper defaults.**
   - Live interval goes from 10 to 30 min and Fast from 2 to 5 min. These are the existing
     `FlightSync:Interval` and `NearDepartureInterval` settings, so this is a config change.
   - Schedule mode is weekly until 7 days before departure, then daily.
   - Schedule checks pass the known `DepartureIata` so they use the 1-page local-day window rather
     than the 3-day widened one. `SyncFlightStatusCommand` currently builds the criteria with no
     origin (spec §2.4).
2. **Cooldown and rate limit on traveler manual sync.**
   - `POST /flights/{id}/sync` returns the stored state without a call if `LastSyncedUtc` is less
     than 5 min old.
   - Add a `flight-sync` rate-limit policy partitioned by traveler id (`Program.cs:203-260`).
3. **Search abuse limits.**
   - Partition `flight-search` by traveler id when authenticated, and by IP only for anonymous
     callers.
   - Add `UseForwardedHeaders` so the IP is the real client behind App Service.
   - Decide whether anonymous search stays. Recommendation: require auth.
4. **Timezone lookup leak.** Cache failed lookups for 1 hour, cache successes for 365 days instead
   of 30, and look up each distinct origin once per search, not once per row
   (`AeroApiAirportTimezoneLookup.cs`, `AeroApiFlightSearchProvider.cs`).
5. **No retries for scheduled polls**, since the next loop is the retry. Keep retries for
   interactive search and sync. This needs a second named client or a per-request resilience
   property.
6. **#303 budget guard.** `IProviderBudgetGuard` in Application reads month-to-date spend from the
   existing cached `/account/usage` call, which is free.
   - At 80% (soft): Live polling doubles its interval, and search is served from cache or the
     fallback provider only.
   - At 100% (hard): only Fast-mode flights and alert processing make calls, and uncached searches
     get a 503 with `Retry-After`.
   - Settings: `AeroApi:MonthlyBudgetUsd` (already exists) plus `SoftLimitRatio`.
7. **Cap on active tracked flights per traveler** (for example 20), checked in
   `TrackFlightCommandValidator`.

## Phase 2: AeroAPI push alerts replace most polling

**Spike first, and ask before running it.** It needs a few alerts on 1–2 real flights, about
$0.10–0.30.
- Set the endpoint with `PUT /alerts/endpoint`, then create alerts with `POST /alerts` for the
  events `departure`, `arrival`, `cancelled` and `diverted`.
- Capture the real payloads: is there a flight object with gate, terminal and estimated times? What
  do the event codes look like? Are bundled changes delivered separately?
- Count deliveries and cost with `/admin/usage/aeroapi`.

**Prerequisite:** #351, where gate, terminal, baggage and ETA changes are dropped as duplicates.
Otherwise alert-delivered gate changes get discarded.

**Build:**
- **Webhook** `POST /webhooks/aeroapi/{secret}`:
  - Anonymous, with `AeroApi:WebhookSecret` from Key Vault compared in constant time.
  - Accepts only alert ids we registered, and is idempotent per (alert id, event, time).
  - Returns 200 quickly. It gets its own rate limit and is excluded from `/admin` auditing.
- **Command** `ApplyFlightAlertCommand` (Application) maps the payload to `FlightStatusResult` and
  reuses the status-apply path in `SyncFlightStatusCommand`: the dedup hash, `FlightStatusEvent`,
  and `FlightStatusSyncedEventHandler` for the APNs push. Extract that path into a shared
  `FlightStatusApplier`, not a copy.
- **Alert lifecycle:**
  - Add `flights.provider_alert_id` (nullable).
  - A handler runs after the first traveler tracks the flight and creates the alert (free).
  - The alert is deleted when the last traveler untracks, or once the flight is Stopped.
  - The sync loop re-creates missing alerts, because a post-commit handler can fail.
- **Planner change:** Fast and Live become **Alerted**. Each flight gets targeted polls only:
  - T−3h and T−1h for gate, terminal and ETA.
  - Arrival +20 min for baggage claim.
  - A watchdog poll if the departure or arrival alert is overdue by more than 60 min, which falls
    back to normal polling.

## Phase 3: AeroDataBox as the second provider (search first)

**Spike first.** Use AeroDataBox's free 400-unit Basic plan (non-commercial use is fine for a
test). Comparing results against AeroAPI costs a few cents, so ask before that part.
- How far ahead does FIDS return future dates?
- Is `withCodeshared` enough to reproduce `IncludeCodeshares` and the one-row-per-designator shape?
- Does `flights/Number` work for future dates?
- Compare results on 10 routes and 10 flight numbers.

**Build:**
- **`AeroDataBoxFlightSearchProvider`**, restored from `git show 21e55cf^:…` and adapted to today's
  `IFlightSearchProvider` contract (`Application/Interfaces/ExternalServices/IFlightSearchProvider.cs`):
  - `ServiceDate` is the origin's local date.
  - An empty list means no data; a `DomainException` means the provider failed.
  - It keeps the single-airport guard.
  - Its raw FIDS page is cached per (origin, local date), and destination and flight-number
    filtering happen on top of the cached page.
- **`RoutingFlightSearchProvider`** (Infrastructure) decides between primary and fallback:
  - It falls back when the provider fails (502 or 503) or the budget guard says the primary is
    exhausted.
  - It **never** falls back on a 400 or an empty result, since "no flights" is a valid answer.
  - Setting: `FlightData:Search:Primary`. The existing `AeroApiFlightSearchCachingDecorator` wraps
    the router, so both providers share one result cache.
- **DI**, following the `"AeroApi"` / `"GitHub"` patterns in `Infrastructure/DependencyInjection.cs`:
  - A `"AeroDataBox"` named client with its API key header.
  - Its own throttle at the plan's requests per second, with resilience timeouts derived from it.
  - `AeroDataBoxOptions` with `ValidateOnStart`. The key is optional: without it the router uses
    AeroAPI only, the way `GitHub:Token` works.
  - A health check tagged `external`.
- **Usage visibility:** read the marketplace quota headers (units remaining) into the admin usage
  view. AeroDataBox bills a fixed number of units per call, so there's no per-page billing
  ambiguity like AeroAPI's.
- **Also:** update CLAUDE.md ("sole provider" → routing) and regenerate `spec/openapi.json` if any
  response changes.

## Phase 4: later, driven by data

- Route status to AeroDataBox (polls or its alerts, 1 credit per flight in a notification) if the
  Phase 3 spike shows comparable quality, or as the fallback when the budget guard's hard stop hits.
- Dedupe codeshares: track the operating flight (`actual_ident_iata`) so TK1028 and 6E4120 cost one
  alert, not two.
- AeroAPI Standard tier: the $100 minimum is spend, not an extra fee. Switch only once usage
  reaches ~$100 anyway or the 10 result sets/min limit bites.
- A single-leader lock for the sync loop before scaling out to more instances (#347).

---

## Verification

- **Cost baseline:** before Phase 1, take `GET /admin/usage/aeroapi` for month to date, broken
  down by operation (free). Repeat after each phase. The per-operation `Pages` and `CostUsd` should
  fall on `/flights/{ident}` (Phases 1–2) and `/schedules` plus `/airports` (Phase 3).
- **Unit tests** (Application.Tests):
  - Planner modes and intervals, including the Alerted mode and its watchdog.
  - Budget guard thresholds.
  - Mapping an alert payload to `FlightStatusResult`, using the payload captured in the spike.
  - Cooldown on traveler sync.
- **Infrastructure tests:**
  - Router fallback rules (fails over on 502/503 and budget, not on 400 or empty), with fake
    handlers.
  - AeroDataBox provider parsing against recorded JSON.
  - Named-client pipeline tests like `AeroApiClientPipelineTests`, so the throttle and timeouts
    compose correctly.
- **API tests:** webhook rejects a wrong secret and unknown alert ids and is idempotent; new
  rate-limit policies return 429.
- **Spikes (Phases 2 and 3) are the only metered steps.** Each needs your explicit go-ahead, with
  the number of calls and expected cost stated first.
- Run `dotnet build`, `dotnet test`, and `scripts/generate-openapi.sh` for any contract change.
