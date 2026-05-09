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

**Bounded contexts:** Travelers, Flights, Loyalty, Lounges, Visa, Configuration (reference data).

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
- `DomainException(message, statusCode=400)` → `GlobalExceptionHandler` maps to `ProblemDetails` at the given status code.
- `ValidationException` → 400 `ValidationProblemDetails` with errors grouped by property name.
- Unhandled exceptions → 500.

**Idempotent commands:** Handlers check for existing state before inserting (e.g., `RegisterTravelerCommand` queries by email first).

**Caching:** `ICacheService` abstraction. Redis is used when `ConnectionString:Redis` is configured; falls back to `IMemoryCache` otherwise. Injected directly into handlers — no decorator or pipeline pattern.

**Flight status providers:** `CompositeFlightStatusProvider` tries AeroDataBox first, falls back to AviationStack on failure. Both clients use Polly: 3 retries, exponential backoff, 30 s timeout.

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
| `AeroDataBox:ApiKey` | RapidAPI key |
| `AviationStack:ApiKey` | Fallback flight provider |
| `Sherpa:ApiKey` | Visa requirements API |
| `Apns:BundleId`, `Apns:KeyId`, `Apns:TeamId`, `Apns:P8KeyContent` | iOS push; `Apns:UseSandbox=true` for dev |
| `AdminPanel:AllowedOrigin` | CORS origin for admin UI (default: `http://localhost:5173`) |

## Tech Stack

- **.NET 8**, ASP.NET Core Minimal APIs, PostgreSQL 16, Redis
- **Auth:** Azure AD B2C / Entra External ID (JWT Bearer via `Microsoft.Identity.Web`)
- **Cloud:** Azure App Service, Key Vault, Application Insights
- **External APIs:** AeroDataBox, AviationStack, Sherpa (visa), OpenFlights (reference data seed)
- **Push notifications:** APNs via dotAPNS (iOS only)
- **CI/CD:** GitHub Actions → Azure Web App on push to `development`; EF migration bundle applied before deploy

## Branching

Branch off from `development` for every feature or fix. Open a PR back into `development`. Never commit directly to `development` or `main`.

## Tests

- `FlightAssistant.Domain.Tests` — unit tests for aggregate behavior via factory methods (constructors are private).
- `FlightAssistant.Application.Tests` — handler-level tests.
- No integration tests yet. Validators are not tested in isolation; they're exercised through the MediatR pipeline behavior.
