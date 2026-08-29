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

**Flight data provider:** AviationStack is the sole external provider for both flight status (`AviationStackFlightStatusProvider`) and flight search (`AviationStackFlightSearchProvider`). The `IFlightStatusProvider` / `IFlightSearchProvider` abstractions are kept so a fallback composite can be reintroduced for production. Clients use Polly: 3 retries (status) / 2 retries (search), exponential backoff, 30 s timeout. Search calls `/v1/flights` with `dep_scheduled_time_dep` (the date) plus either `dep_iata` + `arr_iata` (route search) or `flight_iata` (flight-number search), filtering server-side in a single request. Both providers share `AviationStackResponse.ReadOrThrowAsync`, which inspects the AviationStack `error` body (present even under HTTP 200) and maps `error.code` to a `DomainException`: throttling (`rate_limit_reached`/`usage_limit_reached`) → 503 (with `Retry-After`), credential/plan problems (`invalid_access_key`, `function_access_restricted`, …) → 500, `validation_error` → 400, anything else / unreachable → 502. The upstream code and message are logged and carried in `DiagnosticDetail` (surfaced in the response outside Production); the client-facing `Message` never contains them. A successful search response with no matches returns an empty list, and a status response with an empty `data` array returns `null` — both mean "no data", distinct from a provider failure.

**Flight search date routing:** AviationStack splits its data across two endpoints with no overlap, so `DateRoutingFlightSearchProvider` picks by date (boundary: UTC today) and the split never reaches the API contract:
- `/v1/flights` (`AviationStackLiveSearchProvider`) — past and present. A future date returns an empty set, not an error.
- `/v1/flightsFuture` (`AviationStackScheduleSearchProvider`) — future schedules, up to a rolling ~11.5-month horizon. Requires `iataCode`; ignores `arr_iata`/`flight_iata` (filtered client-side) but honours `airline_iata`. Has no codeshare marker, so `OperatingFlightNumber` is null for future results. Flight-number-only searches resolve a departure airport first via `/v1/routes` (`AviationStackRouteResolver`).

**AviationStack times are airport-local, never UTC.** `/v1/flights` stamps them `+00:00` anyway — a live LHR→JFK reads `dep 20:05+00:00, arr 22:50+00:00`, which as UTC would be a 2h45m transatlantic flight. Always convert via `AirportTimeZoneResolver` (payload `timezone` → `Airport.Timezone` → omit + warn). Parse with `DateTimeOffset.TryParse(...).DateTime`, never `DateTime.TryParse`, which rebases onto the host machine's timezone. **`AviationStackFlightStatusProvider` still has this bug** — see issue #276.

**Search results are grouped per physical departure.** One aircraft is sold under many flight numbers (TK1028 is also 6E4120, MS9668, RJ3710, SQ6228), so `FlightSearchResult.FlightNumbers` lists them all. `/v1/flights` marks codeshares exactly via `flight.codeshared` (null = the operating row); `flightsFuture` does not, hence the nullable `OperatingFlightNumber`.

**Paging:** Input `PagedRequest(Page=1, PageSize=50)`, output `PagedResult<T>(Items, Total, Page, PageSize)`. Validated by `PagedRequestValidator` (Page ≥ 1, PageSize 1–100). Reuse `PagedRequestValidator` in query validators via `.SetValidator(new PagedRequestValidator())`.

**RefDataRepository:** Consolidates all reference data reads (airports, airlines, countries, alliances) in a single repository intentionally — see the file for rationale; planned to split later.

## Conventions

**File layout in Application:**
- Commands: `Application/Commands/{Domain}/{CommandName}.cs` — command record, validator, and handler all in one file.
- Queries: `Application/Queries/{Domain}/{QueryName}.cs` — same pattern.
- Validators: named `{CommandName}Validator`, defined in the same file as the command.

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
| `AviationStack:ApiKey` | Required; sole flight data provider (status + search) |
| `AviationStack:BaseUrl` | Optional; defaults to `http://api.aviationstack.com/v1/` |
| `Apns:BundleId`, `Apns:KeyId`, `Apns:TeamId`, `Apns:P8KeyContent` | iOS push; `Apns:UseSandbox=true` for dev |
| `AdminPanel:AllowedOrigin` | CORS origin for admin UI (default: `http://localhost:5173`) |

## Tech Stack

- **.NET 8**, ASP.NET Core Minimal APIs, PostgreSQL 16, Redis
- **Auth:** Azure AD B2C / Entra External ID (JWT Bearer via `Microsoft.Identity.Web`)
- **Cloud:** Azure App Service, Key Vault, Application Insights
- **External APIs:** AviationStack (flight status + search), OpenFlights (reference data seed)
- **Push notifications:** APNs via dotAPNS (iOS only)
- **CI/CD:** GitHub Actions → Azure Web App on push to `development`; EF migration bundle applied before deploy

## Branching

Branch off from `development` for every feature or fix. Open a PR back into `development`. Never commit directly to `development` or `main`.

## Tests

- `FlightAssistant.Domain.Tests` — unit tests for aggregate behavior via factory methods (constructors are private).
- `FlightAssistant.Application.Tests` — handler-level tests.
- No integration tests yet. Validators are not tested in isolation; they're exercised through the MediatR pipeline behavior.
