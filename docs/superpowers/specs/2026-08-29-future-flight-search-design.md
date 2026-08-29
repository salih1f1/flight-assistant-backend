# Flight Search Across the Full Date Range

**Date:** 2026-08-29
**Status:** Approved

## Problem

`GET /flights/search` returns an empty list for every future date. Its purpose is to populate
the "add flight" picker before `POST /flights`, so searching for a flight you intend to take —
the primary use case — silently returns nothing.

Cause: the provider calls AviationStack `/v1/flights`, which serves **past and present only**.
Verified against a live key for SOF→IST:

| `flight_date` | Result |
|---|---|
| 2026-02-20 (6 months back) | 9 flights |
| 2026-08-20 (9 days back) | 9 flights |
| 2026-08-29 (today) | 9 flights |
| 2026-08-30 (tomorrow) | **0** |
| 2026-10-10 | **0** |

The cutoff sits exactly between today and tomorrow. AviationStack does not document this as a
limitation; it is inferable only from the endpoint's name, the FAQ's description of a backward
"3 month sliding window", and the existence of a separate future endpoint. The empty result is
indistinguishable from "no flights on this route", which is why it went unnoticed.

Note the FAQ's 3-month historical claim understates reality — 6 months back returns data — so
the existing "not more than 1 year in the past" validation rule stays as-is.

## Goals

- `/flights/search` returns correct results for any date the provider can serve, past or future.
- Results are deduplicated to one entry per physical departure, consistently across both ranges.
- Out-of-range dates are rejected with a 400, not an upstream error or a misleading empty list.

## Non-goals

- Live status, gates, or terminals in search results. Those are not in the search contract today
  and belong to the status-sync flow.
- Replacing AviationStack.

## Key findings

`/v1/flightsFuture` is the documented future-schedules endpoint and works on the current plan.
Behaviour established by probing, since the vendor's rendered docs are not machine-readable:

| Property | Value |
|---|---|
| Required params | `iataCode`, `type` (`departure`\|`arrival`), `date` |
| Forward limit | `date must be before 2027-08-15` — ~11.5 months out, rolling |
| Route filtering | **Not supported.** `arr_iata` / `arr_iataCode` silently ignored |
| Flight-number filtering | **Not supported.** `flight_iata` silently ignored |
| Airline filtering | Supported — `airline_iata` narrowed 106 → 3 |
| Airport required | Yes. Omitting `iataCode` returns `validation_error` |
| Times | Airport-local, no offset: `"2026-10-10 09:35:00"` |
| Casing | All values lowercase (`"sof"`, `"tk1028"`) |
| Codeshare marker | **None** |
| Error shapes | Two: `{"error":{"code":...}}` (HTTP 400) and `{"error":"...text..."}` (HTTP 500) |

### Codeshares

`/v1/flights` marks them exactly — `flight.codeshared` names the operating flight, and
`codeshared == null` identifies the operating row:

```
TK1028  codeshared = null
6E4120  codeshared = { flight_iata: "tk1028" }
MS9668  codeshared = { flight_iata: "tk1028" }
```

`/v1/flightsFuture` carries no equivalent, so the operating carrier is learned from a matching
weekday instead — see "Operating carrier" below.

`/v1/flightsFuture` carries no equivalent. The 6E4120 and TK1028 records for 2026-10-10 are
identical apart from their `airline` and `flight` blocks — same gate, terminal, aircraft, and
times. **The operating carrier cannot be determined from future data.** The design admits this
rather than guessing.

Nine SOF→IST rows on 2026-10-10 are three physical departures.

## Design

### Contract

One endpoint, one DTO, one entry per physical departure:

```csharp
FlightSearchResultDto(
    string[] FlightNumbers,          // every number on this departure, uppercase, sorted
    string?  OperatingFlightNumber,  // known for past/today; null for future
    string?  AirlineIata,            // operating airline when known
    string   DepartureIata,
    string   ArrivalIata,
    DateOnly ServiceDate,
    DateTime ScheduledDepartureUtc,
    DateTime ScheduledArrivalUtc,
    string?  EquipmentIcao)
```

`OperatingFlightNumber` is nullable by design: it reports what the provider disclosed instead of
inventing a winner among codeshares. `FlightNumbers` always contains the traveler's number,
whichever carrier issued their ticket.

**This is a breaking change** for consumers of `/flights/search`. `FlightNumber` (singular) and
the non-nullable `AirlineIata` are gone.

The internal `FlightSearchResult` record (`Application/Interfaces/ExternalServices`) changes to
match, since it is the shape every provider produces and the endpoint maps straight from it.

The endpoint keeps its name and route. The two upstream endpoints are a provider implementation
detail; every field above is obtainable from both.

### Providers

```
IFlightSearchProvider
└── DateRoutingFlightSearchProvider
    ├── AviationStackLiveSearchProvider      → /v1/flights
    └── AviationStackScheduleSearchProvider  → /v1/flightsFuture
        └── AviationStackRouteResolver       → /v1/routes
```

Grouping lives inside each leaf provider, because only it knows how to group its own payload:

- **Live**: group by `flight.codeshared?.flight_iata ?? flight.iata`. Exact.
  `OperatingFlightNumber` = the row with `codeshared == null`.
- **Schedule**: group by `(DepartureIata, ArrivalIata, ScheduledDepartureUtc, ScheduledArrivalUtc)`
  after UTC conversion. `OperatingFlightNumber` = `null`.

`DateRoutingFlightSearchProvider` picks by `criteria.Date <= DateOnly.FromDateTime(DateTime.UtcNow)`
→ live, else schedule.

**Known gap.** The two endpoints do not meet. `/v1/flights` stops at today, while
`/v1/flightsFuture` refuses roughly the next three days (observed: 2026-08-31 through 09-02
rejected, 09-03 accepted — and its error text, "date must be above 2026-09-05", is itself wrong).
Dates in between currently surface an upstream 502. For a user east of UTC this also affects
*their* today during the hours when UTC is still on the previous date: searching "today" locally
routes to the schedules endpoint and lands in the gap.

An earlier draft of this spec called that boundary ambiguity harmless on the grounds that both
providers return correct data near it. That was wrong — the schedules endpoint returns nothing
there. Tracked separately rather than fixed here.

### Operating carrier

Schedules repeat weekly and the real-time endpoint marks codeshares exactly, so
`OperatingCarrierResolver` queries the same route on the most recent matching weekday, builds a
map of local departure time → operating flight number, and applies it to the future date.

A learned operator is only accepted when it is one of that group's own flight numbers; otherwise
the reference day's timetable has drifted and the field stays null. The lookup is best-effort —
a failure or an unrecognised route leaves the field null and never fails the search.

`/v1/routes` was evaluated for this and rejected. It resembles an operating/marketing
discriminator on small samples but is not one: 6E4122 is a confirmed codeshare of TK1030 (per the
real-time endpoint's own `codeshared` field) and still returns a route. Its coverage is merely
patchy, so two samples agreeing was luck.

### Timezone conversion

**Both** endpoints report airport-local wall-clock time, never UTC. This was discovered during
implementation and is broader than first scoped:

- `flightsFuture` gives a bare `"2026-10-10 09:35:00"`, obviously local.
- `/v1/flights` gives `"2026-08-29T20:05:00+00:00"` — and **the `+00:00` is decoration**. A live
  LHR→JFK query returns `dep 20:05+00:00, arr 22:50+00:00`, which as UTC is a 2h45m
  transatlantic flight. Read as local (BST→EDT) it is 7h45m, which is correct.

The pre-existing live provider parsed these with `AssumeUniversal`, so **search results have
always been wrong by the departure airport's UTC offset**. Left unfixed, the two providers would
disagree about the same flight either side of today, so the conversion is shared:
`AirportTimeZoneResolver` handles both.

Zone resolution comes **entirely from the provider**, never from our own reference data:
1. The `timezone` the response supplies per airport (the real-time endpoint provides it).
2. AviationStack's `/v1/airports` lookup, for the schedules endpoint, which supplies none.
   Cached for 30 days through `ICacheService`, so this costs at most one call per airport per
   cache window on a rate-limited plan.
3. Otherwise exclude that row — a time silently hours wrong is worse than an absent flight —
   **and if every matching row is dropped this way, throw a 500 naming the airports** rather
   than returning an empty list.

Each candidate must survive `TimeZoneInfo.FindSystemTimeZoneById` before it is accepted. A
present-but-invalid value is worse than a missing one, because it looks authoritative.

`Airport.Timezone` is deliberately **not** consulted, though the column exists. It is imported by
hand, so it can be absent, stale, or hold the wrong thing outright — OpenFlights ships a numeric
GMT offset column beside the IANA one. Two successive defects came from trusting it: first an
empty result for a fully populated timetable when the column was blank, then a hard failure when
a present-but-invalid value shadowed the fallback. The timestamps being converted come from
AviationStack, so the zone that interprets them comes from AviationStack too.

Departure and arrival are converted using their own zones, since a flight crossing zones has a
different one at each end. Times falling in a DST spring-forward gap are nudged forward by an
hour rather than dropped.

Timestamps are parsed with `DateTimeOffset.TryParse(...).DateTime` to take the wall-clock reading
verbatim. `DateTime.TryParse` would rebase an offset-bearing string onto the *host machine's*
timezone, which makes results depend on where the server runs — and would pass on a UTC CI
machine while failing in any other zone.

### Flight-number-only search on future dates

`flightsFuture` requires an airport, so `?date=<future>&flightNumber=TK1028` cannot be served
directly. `AviationStackRouteResolver` calls `/v1/routes?airline_iata=TK&flight_number=1028` to
resolve the departure airport, then queries `flightsFuture` for it.

`/v1/routes` returns a coarser schedule than `flightsFuture` (10:00 vs the actual 09:35), so it
is used **only** to resolve the airport — never for times.

When the route cannot be resolved, return an empty list. The flight genuinely isn't findable.

### Validation

`SearchFlightsQueryValidator` gains a forward bound rejecting dates more than 11 months ahead,
with a message naming the limit. Configurable via `AviationStack:MaxFutureSearchMonths`
(default 11) since the upstream limit is a rolling window.

The existing 1-year backward rule is unchanged.

### Error handling

`AviationStackResponse.ReadOrThrowAsync` (from #273) currently matches only object-shaped
`error` bodies. `flightsFuture` also returns string-shaped ones under HTTP 500, e.g.
`{"error":"date must be before 2027-08-15"}`. Extend the helper to handle a string `error`,
mapping it to 502 with the text carried in `DiagnosticDetail`. Without this the most useful
diagnostic sentence is discarded.

## Testing

| Area | Test |
|---|---|
| Routing | Past/today date → live provider; future date → schedule provider; boundary at UTC today |
| Live grouping | 9 canned rows with `codeshared` → 3 results; `OperatingFlightNumber` = the `codeshared == null` row |
| Live grouping | Flight-number search for a codeshare (`6E4120`) still returns its group |
| Schedule grouping | 9 canned `flightsFuture` rows → 3 results, `OperatingFlightNumber` null, all 9 numbers present across groups |
| Schedule mapping | Lowercase values uppercased; `aircraft.modelCode` → `EquipmentIcao` |
| Timezone | `"2026-10-10 09:35:00"` at SOF (Europe/Sofia, UTC+3 in October) → `06:35Z`; arrival converted using the arrival airport's zone |
| Timezone | Airport with no timezone → result excluded, warning logged |
| Route resolver | Future + flightNumber-only resolves the departure airport; unresolvable → empty list |
| Validation | Date beyond the forward bound → 400 naming the limit |
| Error handling | String-shaped `error` body → 502 with the text in `DiagnosticDetail` |

## Risks

- **Timezone conversion is the highest-risk element.** An error makes every future flight wrong
  by hours while looking plausible. It gets dedicated tests including a DST boundary.
- **Breaking DTO change** requires a coordinated iOS release.
- **Extra upstream call** for future flight-number searches (routes, then flightsFuture),
  against a rate-limited plan.
- **The forward limit is a rolling window**, so a hardcoded bound drifts. Configurable, and the
  upstream error is surfaced rather than swallowed if it is exceeded.
