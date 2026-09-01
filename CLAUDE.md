# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Quick Start

```bash
# Start dependencies
docker compose up -d   # PostgreSQL 16 on :5432, Redis 7 on :6379

# Run API (Swagger at http://localhost:5000/swagger)
dotnet run --project FlightAssistant.API
```

## Commands

```bash
# Build
dotnet build --configuration Release

# Run all tests
dotnet test

# Run tests in a specific project
dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj

# Run a specific test class or test
dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj --filter "ClassName=FlightTests"
dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj --filter "Name~Create_NormalizesFlightNumber"

# EF Core migrations
dotnet ef migrations add <MigrationName> --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
dotnet ef database update --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
```

## Architecture

ASP.NET Core Web API using **Clean Architecture + CQRS** (MediatR) with **Domain-Driven Design**.

**Layers:**
- `FlightAssistant.Domain` — Aggregates, entities, domain events, value objects, exceptions. Private constructors everywhere; use factory methods.
- `FlightAssistant.Application` — MediatR command/query handlers + FluentValidation validators. All use cases live here.
- `FlightAssistant.Infrastructure` — EF Core + PostgreSQL, Redis, HTTP clients for external APIs, APNs.
- `FlightAssistant.API` — Minimal API endpoints, middleware, DI wiring in `Program.cs`, hosted services.
- `FlightAssistant.Contracts` — Request/response DTOs. No dependencies on other layers.
- `FlightAssistant.SharedKernel` — Currently empty; base classes live in `Domain/Common/` instead.

**Bounded contexts:** Travelers, Flights, Loyalty, Lounges, Configuration (reference data).

## Domain Base Classes

All live in `FlightAssistant.Domain/Common/`, **not** SharedKernel (which is empty):

- `BaseEntity` — `Guid Id`, in-memory domain events list, equality by Id.
- `AuditableEntity` — extends `BaseEntity`; adds `CreatedUtc`, `CreatedBy`, `UpdatedUtc`, `UpdatedBy`.
- `DomainEvent` — abstract `record` implementing MediatR `INotification`; carries `OccurredUtc`, optional `CorrelationId`/`CausationId`.
- `IAggregateRoot` — empty marker interface; `Traveler` and `Flight` implement it.
- `ValueObject` — equality by `GetEqualityComponents()`.

## Key Patterns

**Domain events:** Aggregates raise events in-memory via `Raise()`. `DomainEventDispatchInterceptor` publishes them via MediatR *after* `SaveChangesAsync` completes — so handlers always run post-commit and cannot roll back the DB write.

**MediatR pipeline:** `ValidationBehaviour<TRequest,TResponse>` runs before every handler. It collects all registered `IValidator<TRequest>` implementations, groups failures by property name, and throws `ValidationException`. Validators are auto-discovered from the Application assembly — no manual registration needed.

**Error handling:**
- `DomainException(message, statusCode=400, diagnosticDetail=null)` → `GlobalExceptionHandler` maps to `ProblemDetails` at the given status code.
- `ValidationException` → 400 `ValidationProblemDetails` with errors grouped by property name.
- Unhandled exceptions → 500.
- Every problem response carries a `traceId` extension (`Activity.Current?.Id`, falling back to `HttpContext.TraceIdentifier`) for log correlation.
- `DomainException.DiagnosticDetail` is the operator-facing cause (e.g. the upstream provider's error code and message). It is rendered as ProblemDetails `Detail` **only outside Production**; Production callers get the title and `traceId` only. Never put credentials or request URLs in it — the value is client-visible in Development and Staging.

**Idempotent commands:** Handlers check for existing state before inserting (e.g., `RegisterTravelerCommand` queries by email first).

**Caching:** `ICacheService` abstraction. Redis is used when `ConnectionString:Redis` is configured; falls back to `IMemoryCache` otherwise. Injected directly into handlers — no decorator or pipeline pattern.

**Flight data provider:** FlightAware AeroAPI is the sole external provider for both flight status (`AeroApiFlightStatusProvider`) and flight search (`AeroApiFlightSearchProvider`). The `IFlightStatusProvider` / `IFlightSearchProvider` abstractions are kept so a fallback composite can be reintroduced for production. Both providers share one named `"AeroApi"` `HttpClient`, authenticated via the `x-apikey` header (never a URL query string, so there is nothing to sanitise out of telemetry), with Polly: `AeroApi:MaxRetryAttempts` retries (default 2), exponential backoff — except a 429 response, which is never auto-retried, since retrying a throttle response on a metered tier only makes it worse. All AeroAPI timestamps are UTC already (no timezone conversion needed on the way in). Both providers share `AeroApiResponse.ReadOrThrowAsync`, which reads the real HTTP status code AeroAPI signals failures with (no `error` body hidden under HTTP 200) and maps it to a `DomainException`: 401/403 (credential/plan problems) → 500, 429 (throttled) → 503 (with `Retry-After`), 400 (rejected parameters) → 400, anything else / unreachable / unparseable → 502. The upstream body — redacted of anything credential-shaped — is logged and carried in `DiagnosticDetail` (surfaced in the response outside Production); the client-facing `Message` never contains it. A successful search response with no matches returns an empty list, and a status response with no matching row returns `null` — both mean "no data", distinct from a provider failure.

**Flight search:** `AeroApiFlightSearchProvider` calls one endpoint, `GET /schedules/{date_start}/{date_end}`, covering a single rolling window from `AeroApi:MaxPastMonths` (default 3) months ago to `AeroApi:MaxFutureMonths` (default 12) months ahead — there is no live/future split to route between. `origin`, `destination`, `airline`, `flight_number` and `include_codeshares` all filter server-side, so there is no client-side route/flight-number filtering. AeroAPI paginates at roughly 15 rows per page; `AeroApi:MaxPages` (default 4) caps how many pages a single search fetches, bounding cost on a metered tier — a search that hits the cap logs a warning and returns what it has rather than fetching further pages. Raw schedule rows carry `actual_ident_iata`, which names the operating carrier's flight number on a codeshare row (null on the operating row itself); `IncludeCodeshares` just forwards to AeroAPI's own `include_codeshares` parameter, and the result stays one row per flight designator — `FlightSearchResult` deliberately carries no operating-carrier grouping.

**The local-day window shift.** `scheduled_out`/`scheduled_in` are UTC, but a search's `Date` is a local service date at the *origin* airport — a naive `[D, D+1)` UTC query can be wrong by nearly half a day for a far-east origin. `AeroApiFlightSearchProvider` resolves the origin's IANA zone via `AeroApiAirportTimezoneLookup` (`GET /airports/{id}`, cached 30 days) and shifts the query window to the exact UTC instants of local midnight and next-local-midnight, keeping it a strict 24 hours so page count is unaffected. When the origin (or its zone) is unknown — a flight-number-only search may carry no origin at all — the window widens to `[D−1, D+2)` UTC instead, and each row is then filtered by its own `origin_iata`'s zone once the response supplies it. If a row's own zone still cannot be resolved, the row is kept rather than dropped (the #275 principle: an unplaceable time is worse omitted than approximated), falling back to a UTC-calendar-date comparison as a last resort.

**`ServiceDate` is origin-local everywhere, status included.** Search reports the origin's local service date, `TrackFlightCommandHandler` stores it verbatim, and `SyncFlightStatusCommand` hands it back to `AeroApiFlightStatusProvider` — which therefore picks its matching `/flights/{ident}` row by converting `scheduled_out` into **that row's own `origin.timezone`** (already present in the payload, so no extra call) before comparing. Matching on the UTC calendar date instead selects the wrong day's operation for any airport far enough from UTC — AKL at UTC+13 departing 07:00 local is 18:00 Z the day before — and silently applies the next day's gate, terminal and status. A missing or unloadable zone falls back to the UTC comparison.

**Search guard:** `AeroApiFlightSearchProvider` refuses any query that is not anchored on **both** ends of a route or on a flight number long enough to push down as `airline`+`flight_number` (≥ 2 chars). A single-airport query is ~700 rows in one call, and destination-only is worse than origin-only: with no origin there is no zone to anchor a local day on, so it runs over the widened 3-day window. The validator blocks these at the API edge; this guard is the backstop for every other caller.

**`links.next` is untrusted.** The `"AeroApi"` client attaches `x-apikey` to every request regardless of host, so a paging link is followed only if it resolves *underneath* `HttpClient.BaseAddress` — which rejects absolute, host-rooted and `../` links in one test. (Do not test "does it parse as absolute": `Uri.TryCreate("/schedules/…", UriKind.Absolute, …)` succeeds on Unix as a `file://` URI and would reject AeroAPI's own ordinary links.)

**Usage and spend come from the provider, never from a local meter.** `GET /admin/usage/aeroapi` proxies AeroAPI's own `GET /account/usage` via `AeroApiAccountUsageProvider` (same named client, same throttle, same `AeroApiResponse` error mapping), cached 10 minutes — FlightAware's own refresh cadence — under the resolved range. Do not reintroduce a local call-counting table (attempted and closed in PR #294): FlightAware bills per **page**, not per call, and bills **failed** calls too, so anything counted and priced locally is wrong in both directions and can never be reconciled against the invoice. `operation` arrives pre-normalised upstream, and `/account/usage` is itself billed at $0.00. The query defaults to the current UTC month to date with the end floored onto the 10-minute cache grid — an unrounded "now" would change the cache key on every request and spend a metered call each time — and its validator rejects a start older than AeroAPI's one-year history cap up front. `AeroApi:MonthlyBudgetUsd` and `AeroApiThrottleGate.NextSlotUtc` reach the Application handler through `IAeroApiBudgetOptions` / `IAeroApiThrottleObserver` (the `IFlightSearchHorizonOptions` translation pattern); both are attached fresh per request rather than cached alongside the upstream payload.

**Shared outbound throttle:** `AeroApiThrottleGate`, a process-wide singleton applied via `AeroApiThrottlingHandler` to every AeroAPI `HttpClient`, spaces every outbound call — search or status — by `AeroApi:MinRequestInterval` (default 12 s, ≈5 requests/minute) to stay inside the personal tier's rate limit, regardless of which caller sent it. A caller reserves the next slot under a lock and waits it out without holding the lock; a reservation whose wait is cancelled is **released**, so an abandoned attempt never pushes the queue further out for everyone behind it.

**The throttle sits *inside* the resilience handler** (registered second, so it is closer to the transport) — deliberately, so every retry attempt is throttled too. The consequence is that a throttled wait of up to a whole `MinRequestInterval` elapses *within* the per-attempt timeout, so **every resilience timeout is derived from `MinRequestInterval`, never left at a library default or hardcoded** (`AeroApiOptions.AttemptTimeout` = interval + `TimeoutSeconds`; `TotalRequestTimeout` covers every attempt plus backoff; `CircuitBreakerSamplingDuration` ≥ 2× attempt; `HttpClient.Timeout` is a looser backstop outside all of it). With the library's 10 s default against a 12 s interval, every second call in an interval window was killed before it reached the transport, retried twice, and then failed the 30 s total timeout — surfacing as a 502 after a 30-second hang, and breaking every multi-page search from page 2 on. `AeroApiClientPipelineTests` sends real requests through the composed named client (on a `FakeTimeProvider`) because no test that only *resolves* DI objects, or that builds a bare `HttpClient`, can see this.

**Paging:** Input `PagedRequest(Page=1, PageSize=50)`, output `PagedResult<T>(Items, Total, Page, PageSize)`. Validated by `PagedRequestValidator` (Page ≥ 1, PageSize 1–100). Reuse `PagedRequestValidator` in query validators via `.SetValidator(new PagedRequestValidator())`.

**RefDataRepository:** Consolidates all reference data reads (airports, airlines, countries, alliances) in a single repository intentionally — see the file for rationale; planned to split later.

## Conventions

**File layout in Application:**
- Commands: `Application/Commands/{Domain}/{CommandName}.cs` — command record, validator, and handler all in one file.
- Queries: `Application/Queries/{Domain}/{QueryName}.cs` — same pattern.
- Validators: named `{CommandName}Validator`, defined in the same file as the command.
- A format rule shared by more than one validator lives in **one** constant, not two copies of a regex. `Application/Validators/FlightNumberFormat.cs` holds the IATA flight designator pattern used by both `SearchFlightsQueryValidator` and `TrackFlightCommandValidator`, plus a `Description` the Swagger text is rendered from — they had drifted in *both* directions (search accepted an all-digit prefix track refused; track accepted a trailing letter search refused), which is exactly the "search returns a row `POST /flights` rejects" defect the search reshape exists to remove.

**Repository contract:** `SaveChangesAsync()` is called explicitly inside handlers. There is no implicit unit-of-work auto-save.

**External service options:** All external service config uses the Options pattern — `AddOptions<T>().BindConfiguration(section).ValidateOnStart()`.

**Database:**
- Table names: `snake_case` (e.g., `travelers`, `device_tokens`, `traveler_flights`).
- All timestamps: UTC only, suffixed `Utc` (e.g., `CreatedUtc`, `ScheduledDepartureUtc`), stored as `timestamptz`.

**Naming:**
- Commands return a value or `Unit`; queries return data — both are `IRequest<T>`.
- Result/DTO types: `{Name}Result` or `{Name}Dto`.
- Endpoint tag group names match the bounded context (e.g., `"Travelers"`, `"Flights"`).

## Authentication

- JWT Bearer via Azure AD B2C / Entra External ID.
- Traveler identity = `oid` claim (falls back to `sub`), parsed as `Guid`. Use `ClaimsPrincipalExtensions.GetTravelerId()`.
- Scope enforcement: `http.VerifyUserHasAnyAcceptedScope("access_as_user")` at the start of each user-facing handler.
- Admin endpoints use the `"AdminOnly"` authorization policy: authenticated + role claim `"admin"`.
- `TravelerContextMiddleware` enriches Serilog's `LogContext` with TravelerId for every authenticated request.

## Required Configuration

Most values are pulled from Azure Key Vault in deployed environments; for local dev set them in `appsettings.Development.json` or user secrets:

| Key | Notes |
|---|---|
| `PostgresConnectionString` | Required; app won't start without it |
| `ConnectionString:Redis` | Optional; falls back to in-memory cache |
| `Azure:TenantId`, `Azure:ClientId` | B2C tenant and API app registration |
| `Azure:Instance`, `Azure:Domain`, `Azure:Issuer`, `Azure:Authority` | B2C endpoint URLs |
| `AeroApi:ApiKey` | Required; sole flight data provider (status + search) |
| `AeroApi:BaseUrl` | Optional; defaults to `https://aeroapi.flightaware.com/aeroapi/` |
| `AeroApi:MinRequestInterval` | Optional; default `00:00:12` (1 s–5 min). Every resilience timeout is derived from it — tune it and they follow |
| `AeroApi:TimeoutSeconds` | Optional; default 30. Per-attempt **network** budget, *excluding* the throttle wait added on top |
| `AeroApi:MonthlyBudgetUsd` | Optional; default 5. Reference line for the admin usage gauge only — every cost figure comes from AeroAPI's own report, nothing is priced locally |
| `Apns:BundleId`, `Apns:KeyId`, `Apns:TeamId`, `Apns:P8KeyContent` | iOS push; `Apns:UseSandbox=true` for dev |
| `AdminPanel:AllowedOrigin` | CORS origin for admin UI (default: `http://localhost:5173`) |

## Tech Stack

- **.NET 10**, ASP.NET Core Minimal APIs, PostgreSQL 16, Redis
- **Auth:** Azure AD B2C / Entra External ID (JWT Bearer via `Microsoft.Identity.Web`)
- **Cloud:** Azure App Service, Key Vault, Application Insights
- **External APIs:** FlightAware AeroAPI (flight status + search), OpenFlights (reference data seed)
- **Push notifications:** APNs via dotAPNS (iOS only)
- **CI/CD:** GitHub Actions → Azure Web App on push to `development`; EF migration bundle applied before deploy

## Branching

Branch off from `development` for every feature or fix. Open a PR back into `development`. Never commit directly to `development` or `main`.

## Tests

- `FlightAssistant.Domain.Tests` — unit tests for aggregate behavior via factory methods (constructors are private).
- `FlightAssistant.Application.Tests` — handler-level tests.
- No integration tests yet. Validators are not tested in isolation; they're exercised through the MediatR pipeline behavior.
