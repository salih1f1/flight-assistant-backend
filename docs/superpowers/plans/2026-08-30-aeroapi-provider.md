# Implementation plan — AeroAPI provider migration (#280)

Spec: `docs/superpowers/specs/2026-08-30-aeroapi-migration-design.md`

## Global Constraints

These bind every task. Violating one is a spec failure.

- **Base URL** `https://aeroapi.flightaware.com/aeroapi/` (trailing slash). Config section
  `AeroApi`. Key `AeroApi:ApiKey` (Azure Key Vault secret `AeroApi--ApiKey`).
- **Auth is the header `x-apikey`.** Never put the key in a URL or query string.
- **Never issue an unfiltered airport query.** Every `/schedules` call carries at least one of
  `destination` or `flight_number` alongside `origin`. `origin=LHR` alone is ~700 rows ≈ 47
  billable result sets ≈ 4% of the monthly budget.
- **Times from AeroAPI are already UTC** (`2026-09-29T06:35:00Z`). Parse with
  `DateTimeOffset.Parse(...).UtcDateTime`. Never `DateTime.Parse`. Never convert timezones.
- **No live network calls in tests.** Use the fixtures in
  `FlightAssistant.Infrastructure.Tests/Fixtures/AeroApi/` with a stub `HttpMessageHandler`.
  These are real recorded responses.
- Follow existing project conventions: Options pattern with `ValidateOnStart`, `DomainException`
  for provider failures, `snake_case` DB names, `Utc` suffix on timestamps.
- `DomainException.Message` is client-facing and must never contain upstream text or the API
  key. Upstream cause goes in `DiagnosticDetail` only.
- Run `dotnet build --configuration Release` and `dotnet test` before reporting DONE. Paste real
  output. Baseline is **450 tests passing**; tests deleted with AviationStack reduce that count,
  which is expected — never leave a *failure*.

### AeroAPI response shapes (recorded, authoritative)

`GET /schedules/{start}/{end}` → `{ "scheduled": [...], "links": {"next": "..."}, "num_pages": N }`

Each row: `ident_iata`, `ident_icao`, `actual_ident_iata` (**null ⇒ this row is the operating
flight**; non-null ⇒ this row is a codeshare and the value is the operator's ident),
`origin_iata`, `destination_iata`, `scheduled_out`, `scheduled_in`, `aircraft_type`,
`fa_flight_id` (null beyond a few days out).

Query params: `origin`, `destination`, `airline`, `flight_number` (integer), `include_codeshares`
(default true), `include_regional`, `max_pages` (default 1), `cursor`.
**Page size is 15**; `links.next` present ⇒ more pages exist.

`GET /flights/{ident}` → `{ "flights": [...] }`, each with `ident_iata`, `status`,
`scheduled_out|off|on|in`, `estimated_*`, `actual_*`, `gate_origin`, `terminal_origin`,
`baggage_claim`, `aircraft_type`, `registration`, `cancelled`, `diverted`, `codeshares_iata`,
`operator_iata`, and `origin`/`destination` as **objects** with `code_iata` and `timezone`.

---

## Task 1 — Options, HTTP wiring, error mapping, throttle

Create `FlightAssistant.Infrastructure/ExternalServices/AeroApi/`:

**`AeroApiOptions.cs`** — bound from section `AeroApi`, registered with
`AddOptions<AeroApiOptions>().BindConfiguration("AeroApi").ValidateDataAnnotations().ValidateOnStart()`:

| Property | Type | Default | Notes |
|---|---|---|---|
| `ApiKey` | `string` | — | `[Required]`, `[MinLength(1)]` |
| `BaseUrl` | `string` | `https://aeroapi.flightaware.com/aeroapi/` | |
| `MaxPages` | `int` | `4` | `[Range(1, 20)]` — caps billable queries per search |
| `MinRequestInterval` | `TimeSpan` | `00:00:12` | Personal tier is ~5 queries/minute |
| `MaxFutureMonths` | `int` | `12` | `[Range(1, 12)]` |
| `MaxPastMonths` | `int` | `3` | `[Range(1, 3)]` |
| `TimeoutSeconds` | `int` | `30` | |

**`AeroApiResponse.cs`** — `internal static Task<JsonDocument> ReadOrThrowAsync(HttpResponseMessage, string operation, string criteria, ILogger, CancellationToken)`.
Mirrors the contract of the existing `AviationStackResponse` (read it first) but for real HTTP
status codes:

| Upstream | → `DomainException` status | Client-facing message |
|---|---|---|
| 200 | — | returns the parsed document |
| 401, 403 | 500 | "Flight data is temporarily unavailable." |
| 429 | 503 | "Flight search is busy. Please try again shortly." + `Retry-After` when present |
| 400 | 400 | "The flight search request was rejected." |
| other / unreachable | 502 | "Flight search is temporarily unavailable…" |

Upstream status and body go to `DiagnosticDetail` and the log, never to `Message`. Redact
anything resembling the key.

**`AeroApiThrottlingHandler.cs`** — a `DelegatingHandler` registered on **all** AeroAPI clients,
holding a **single shared** gate so the limit is global across endpoints, not per-client.
Implement as a singleton holding a `SemaphoreSlim(1,1)` plus the last-send timestamp; inject
`TimeProvider` so tests do not sleep. Waits `MinRequestInterval` since the previous send.

**DI** in `FlightAssistant.Infrastructure/DependencyInjection.cs` — register the options, the
throttle singleton + handler, and a named `HttpClient` for AeroAPI with `BaseAddress`,
`x-apikey` default header, the timeout, and `AddStandardResilienceHandler` configured so
**429 is NOT retried** (`options.Retry.ShouldHandle` excludes it) and
`MaxRetryAttempts = 2`. Do not remove any AviationStack registration yet — Task 5 does that.

**Tests** (`FlightAssistant.Infrastructure.Tests/ExternalServices/AeroApi/`):
- each status code maps to the documented `DomainException` status
- `Message` never contains the API key or upstream body; `DiagnosticDetail` does carry the cause
- two sends through the shared gate are spaced ≥ `MinRequestInterval` using a fake `TimeProvider`
- sends through *different* clients share one gate
- a missing `AeroApi:ApiKey` fails `ValidateOnStart`

---

## Task 2 — `AeroApiFlightSearchProvider`

Implements the existing `IFlightSearchProvider`. **Keep `FlightSearchResult` as-is in this
task** — Task 4 reshapes it.

**`AeroApiAirportTimezoneLookup.cs`** — `GET /airports/{iata}` → IANA `timezone`. Cached via
`ICacheService` under `aeroapi:airport-tz:{IATA}` for **30 days**, mirroring the existing
`AviationStackAirportTimezoneLookup` (read it for the pattern). Cache successes only.

**The local-day window shift — the core of this task.** `/schedules` windows on `scheduled_out`
in UTC; `FlightSearchCriteria.Date` is a local service date at the **origin**. Resolve the
origin's IANA zone, then convert local midnight and next-local-midnight to UTC instants and pass
both as full datetimes (`yyyy-MM-ddTHH:mm:ssZ`). The window stays 24 hours — **do not widen it**,
which triples pages and cost. If the zone cannot be resolved, fall back to `[D−1, D+2)` UTC and
filter rows to the intended local date, logging a warning.

Proven by fixtures: `schedules-utc-window-day.json` and `schedules-utc-window-daybefore.json` are
AKL→SYD (UTC+12). A naive `[D, D+1)` UTC query returns 6 rows departing the *next* local day and
misses 4 departing 06:00–08:00 on the requested day.

**Query building.** Push down `origin`, `destination`, and — for a flight-number search —
`airline` (the 2-char prefix) and `flight_number` (the integer remainder). Never emit an
unfiltered airport query; if criteria yield neither `destination` nor `flight_number`, throw a
400 `DomainException`.

**Paging.** Follow `links.next` up to `MaxPages`. Stop when `links.next` is absent. If the cap
is hit while `next` is still present, log a warning naming origin, destination and date. Each
page is a billable query — never page without the cap.

**Mapping.** One `FlightSearchResult` per row for now: `FlightNumbers` = `[ident_iata]`,
`OperatingFlightNumber` = `actual_ident_iata ?? ident_iata`, `AirlineIata` = first two chars of
`ident_iata`, times parsed as UTC, `EquipmentIcao` = `aircraft_type`. Skip rows missing
`ident_iata`, `origin_iata`, `destination_iata`, `scheduled_out` or `scheduled_in`.

Register as `IFlightSearchProvider` **replacing** `DateRoutingFlightSearchProvider`.

**Tests** using the fixtures: codeshare rows map with the right operator; `include_codeshares`
reaches the URL; the window shift produces the correct UTC instants for AKL; paging follows
`next` and stops at `MaxPages`; an unfiltered-airport search is rejected; malformed rows are
skipped rather than throwing.

---

## Task 3 — `AeroApiFlightStatusProvider`

Implements `IFlightStatusProvider` via `GET /flights/{ident}`. `ProviderName => "AeroAPI"`.

Pick the row matching `serviceDate` against `scheduled_out`'s date. Map: `status` → `StatusCode`
(normalise to the existing `FlightStatus.Code` vocabulary — `SCH`/`DEP`/`ARR`/`DLY`/`CNL`; read
`FlightAssistant.Domain` for the exact set and the current AviationStack mapping),
`estimated_out`/`estimated_in`, `actual_out`/`actual_in`, `gate_origin` → `Gate`,
`terminal_origin` → `Terminal`, `baggage_claim`, `aircraft_type` → `EquipmentIcao`,
`fa_flight_id` → `ExternalId`, raw JSON → `RawPayloadJson`. `cancelled: true` maps to the
cancelled code regardless of `status`. Return `null` when no row matches the service date —
"no data" is distinct from a failure.

**`ProviderName` changes from `"AviationStack"` to `"AeroAPI"`, and it feeds the stored dedup
hash** (`FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs:48,55`). Do not
silently change behaviour: add a test documenting that existing events keep their old hash and
one duplicate event per tracked flight may be emitted once after deploy. Note it in the report.

**Tests** against `flight-status.json`: field mapping, UTC parsing, cancelled handling, no-match
returns null, status-code normalisation.

---

## Task 4 — Flatten the search contract

**`FlightAssistant.Application/Interfaces/ExternalServices/IFlightSearchProvider.cs`** — replace
`FlightSearchResult` with:

```csharp
public record FlightSearchResult(
    string FlightNumber,
    DateOnly ServiceDate,
    string DepartureAirportIata,
    string ArrivalAirportIata,
    DateTime ScheduledDepartureUtc,
    DateTime ScheduledArrivalUtc);
```

Add `bool IncludeCodeshares` to `FlightSearchCriteria`. Strip every AviationStack reference from
the XML docs in this file.

**`FlightSearchResultDto`** — the same six members, same names, same order.

**`TrackFlightRequest` and `TrackFlightCommand`** — drop `AirlineIata`. `TrackFlightCommandHandler`
derives the airline from `FlightNumber[..2]` when resolving `airlineId` via `IRefDataRepository`
(unchanged 404 behaviour when unknown). `TrackFlightCommand` keeps `TravelerId` plus the six.

**`TrackFlightCommandValidator`** — relax the flight-number rule to
`^[A-Z0-9]{2}\d{1,4}[A-Z]?$` so `6E4120` and `U21234` validate. Delete the `AirlineIata` rule.

**`SearchFlightsQuery`** — add `bool IncludeCodeshares = false`. Delete
`private const int MaxFutureMonths = 11`; the validator reads the horizon from `AeroApiOptions`
(inject `IOptions<AeroApiOptions>`) using `MaxFutureMonths` and `MaxPastMonths`.

**`FlightEndpoints.cs`** — bind `includeCodeshares` (default `false`) on `GET /flights/search`,
map the new DTO, and rewrite both endpoint descriptions: no AviationStack mentions (`:36`,
`:174`), document the flag, and state that a search result posts directly to `POST /flights`.

**Tests:** a `SearchFlightsQueryHandler` test and an endpoint mapping test (**neither exists
today**) asserting a returned row deserialises into a valid `TrackFlightRequest`; a
`ValidatorEdgeCaseTests` case proving `6E4120` passes `TrackFlightCommandValidator`; a handler
test proving the airline is derived from the flight number. Update `TrackFlightCommandHandlerTests`
and `FlightEndpointTests` for the new shapes.

---

## Task 5 — Delete AviationStack, rewire, document

Delete:
- `FlightAssistant.Infrastructure/ExternalServices/FlightSearch/` — all 5 files
- `FlightAssistant.Infrastructure/ExternalServices/AviationStack/` — all 3 files
- `FlightAssistant.Infrastructure/ExternalServices/FlightStatus/AviationStackFlightStatusProvider.cs`
- `FlightAssistant.Infrastructure/HealthChecks/AviationStackHealthCheck.cs`
- `FlightAssistant.API/Telemetry/SanitizeAviationStackTelemetryProcessor.cs` and its registration
  at `Program.cs:124`
- the corresponding tests: `ExternalServices/FlightSearch/*`,
  `ExternalServices/FlightStatus/AviationStackFlightStatusProviderErrorTests.cs`,
  `FlightAssistant.API.Tests/Telemetry/SanitizeAviationStackTelemetryProcessorTests.cs`
- AviationStack assertions in `FlightAssistant.API.Tests/Middleware/GlobalExceptionHandlerTests.cs:41,136`
  (rewrite for AeroAPI, do not just delete the coverage)

Add `AeroApiHealthCheck` registered as `"aeroapi"` with tag `external` (`Program.cs:132`), using
`https://` consistently.

Remove every remaining `AviationStack` string from `DependencyInjection.cs`. `FlightTests.cs`
uses `"AviationStack"` as a `source` literal — change to `"AeroAPI"`.

Update docs: `CLAUDE.md` (rewrite the provider sections at lines 78-86 and the config table at
130-131; **also correct "`.NET 8`" → `.NET 10`**, which the csproj files have targeted since the
dotnet-10 migration), `README.md:56`, `FlightAssistant.API/api.http:80`,
`FlightAssistant.postman_collection.json:327`.

Verify: `dotnet build --configuration Release` and `dotnet test` both clean, and `grep -ri
aviationstack` returns only historical design docs under `docs/`.
