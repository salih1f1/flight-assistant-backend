# Migrating the flight data provider to FlightAware AeroAPI

**Status:** Approved — Task 1 (evaluation) complete, verdict **GO**
**Date:** 2026-08-30
**Issue:** #278
**Supersedes the provider decisions in:** `2026-08-29-future-flight-search-design.md`

## Why

`21e55cf` chose AviationStack because `/v1/flights` filters by `dep_iata + arr_iata +
flight_date` server-side in one request. That premise holds only for past and present. The
future-flight requirement (#275) arrived afterwards, and `/v1/flightsFuture`:

- cannot filter by arrival airport or flight number (both silently ignored),
- does not disclose the operating carrier,
- is rate-limited to 1 request per 10 seconds.

So every future-dated search fetches an entire airport departure board and filters client-side:
4–8 upstream calls typically, **up to 62** worst case, which at 1 req/10 s is ten minutes of
wall clock. `OperatingCarrierResolver` exists solely to *guess* the operating carrier by
re-querying a week-old departure board and pattern-matching local departure times.

AeroAPI's `GET /schedules/{date_start}/{date_end}` removes the premise: `origin`,
`destination`, `airline`, `flight_number` and `include_codeshares` are all server-side, and
`actual_ident_iata` names the operator outright.

## Evaluation results (10 live queries, ~$0.03)

### Confirmed working

**Codeshare fidelity is exact.** `airline=TK&flight_number=1028` on 2026-09-29 returned SOF→IST
with precisely the five idents this repo's own 2026-08-29 spec documents:

| `ident_iata` | `actual_ident_iata` |
|---|---|
| TK1028 | `null` ← the operating row |
| 6E4120 | TK1028 |
| MS9668 | TK1028 |
| RJ3710 | TK1028 |
| SQ6228 | TK1028 |

`include_codeshares=false` returned exactly one row (TK1028). AeroAPI reproduces, as published
data, the codeshare set AviationStack needed a heuristic to guess.

**The codeshare signal is binary, not tri-state.** `actual_ident_iata == null` means "this row
is the operating flight"; non-null names the operator. There is no "provider doesn't disclose"
state, so the `CodeshareRole.Unknown` case planned earlier is **not needed** — drop it.

**Horizon confirmed.** LHR→JFK returned data at today+2, today+30 and today+300 (2027-06-26).

**Times are UTC on both endpoints.** `scheduled_out`/`scheduled_in` are `...Z` throughout. The
`AirportTimeZoneResolver` / `AviationStackAirportTimezoneLookup` machinery is not ported, and
issue #276 dissolves rather than needing a fix.

**Status data is richer than `FlightStatusResult` requires.** `/flights/{ident}` supplies
`status`, `scheduled/estimated/actual_out|off|on|in`, `gate_origin`, `terminal_origin`,
`baggage_claim`, `aircraft_type`, `registration`, `cancelled`, `diverted`, `progress_percent`,
`departure_delay`, `arrival_delay`, plus `codeshares_iata` and `operator_iata`. Origin and
destination arrive as objects carrying `code_iata` **and `timezone`**.

### Constraint 1 — page size is 15 and `max_pages` defaults to 1

Every route query returned exactly 15 rows with `links.next` populated: **all of them were
silently truncated.** Both LHR→JFK variants (codeshares on and off) hit the cap, so the
codeshare flag's effect was invisible at page 1.

One page = 15 records = one result set = **one billable query**. The cursor must be followed, and
page count drives cost directly.

**Revised budget.** A route search is realistically 3–6 pages, not 1. At $0.002–0.005 per query
that is roughly $0.01–0.03 per uncached search, so the $5/month tier funds on the order of
**150–500 uncached searches**, not the 1,000–2,500 estimated before this probe. Caching is a
requirement, not an optimisation. Cap `max_pages` and surface truncation rather than paging
without limit.

### Constraint 2 — the UTC window does not match a local service date

`/schedules` windows on `scheduled_out` in UTC; `ServiceDate` is a local-day concept. Measured
on AKL→SYD (Auckland, UTC+12), querying `[2026-09-29, 2026-09-30)` UTC:

- returned 13 rows, of which **6 actually depart on 30 Sept local** (17:15–20:20Z → 05:15–08:20
  local next day);
- **missed 4 flights that depart on 29 Sept local** — NZ101, QF140, QF4, NZ103 at 06:00–08:00
  local — because they fall at 18:00–20:00Z on **28 Sept**.

That is roughly 46% wrong for a UTC+12 airport, and the missed flights are early-morning
departures a traveler is very likely to be searching for. This is the same bug class as the
date-boundary defect the previous spec retracted.

**Fix — shift the window, don't widen it.** `date_start`/`date_end` accept full datetimes
(`2021-12-31T19:59:59Z`), so pass the exact UTC instants of local midnight → local midnight at
the **origin** airport. That is still a 24-hour window: no extra rows, no extra pages, no extra
cost, and exactly correct. Widening to `[D−1, D+2)` and filtering would triple the page count
and therefore the bill — reject it.

Resolving the origin's IANA zone: AeroAPI `/airports/{id}` returns `timezone`, cached 30 days
via the existing `ICacheService` pattern (`airport-tz:{iata}`). `Airport.Timezone` in reference
data stays unused for this — it is hand-imported and may be stale, and the previous spec's
reasoning for distrusting it still applies. Fall back to a `[D−1, D+2)` widen-and-filter only
when the zone cannot be resolved.

## Consequences for the migration

Deleted outright, all of it AviationStack scar tissue:

| Deleted | Because |
|---|---|
| `AviationStackLive/ScheduleSearchProvider`, `DateRoutingFlightSearchProvider` | one endpoint covers −3 mo → +12 mo |
| `OperatingCarrierResolver` | `actual_ident_iata` |
| `AviationStackRouteResolver` | `flight_number` is a native server-side filter |
| `AirportTimeZoneResolver`, `AviationStackAirportTimezoneLookup` | times are UTC |
| `AviationStackResponse` | AeroAPI uses real HTTP status codes, not errors inside HTTP 200 |
| `SanitizeAviationStackTelemetryProcessor` (+ tests) | AeroAPI authenticates by `x-apikey` header, not a URL query param |
| ~880 lines of provider grouping tests | the behaviour moves server-side |

Kept: the `IFlightSearchProvider` / `IFlightStatusProvider` seams and the `DomainException`
status-mapping contract (502/503/500/400) with its `DiagnosticDetail` rule that client-facing
`Message` never carries upstream text.

**No `IAirportDepartureBoardSource` abstraction.** An earlier draft proposed one to make
whole-board fetching cheap; AeroAPI filters server-side, so that premise is gone and the layer
would solve a problem we no longer have.

**Never issue an unfiltered airport query.** `origin=LHR` with no `destination` is ~700 rows
≈ 47 result sets — about 4% of the monthly budget in a single call. Every query must be route-
or flight-scoped.

**Handle deliberately:** `IFlightStatusProvider.ProviderName` is folded into the stored dedup
hash (`SyncFlightStatusCommand.cs:48,55`), so changing it from `"AviationStack"` changes stored
hashes and re-emits already-seen status events. Decide migration vs. accepted one-off
duplicates when the default flips.

## Verified facts, for reference

From the published OpenAPI spec (v4.17.1), no quota spent:

- `GET /schedules/{date_start}/{date_end}` — `origin`, `destination`, `airline`,
  `flight_number`, `include_codeshares` (default **true**), `include_regional` (default true),
  `max_pages` (default **1**), `cursor`.
- `date_end` must be ≤ 1 year ahead and ≤ 3 weeks after `date_start`. A bare date means
  `00:00:00Z`.
- Range: 3 months past → 1 year future.
- `fa_flight_id` is null for flights more than a few days out, so it cannot serve as
  `Flight.ExternalId` for future flights. It is optional on the aggregate and unused today;
  leave it that way.
