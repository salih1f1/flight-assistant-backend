# ✈️ Flight Assistant

[![CI](https://github.com/salih1f1/flight-assistant-backend/actions/workflows/ci.yml/badge.svg?branch=development)](https://github.com/salih1f1/flight-assistant-backend/actions/workflows/ci.yml)  
[![.NET](https://img.shields.io/badge/.NET-10.0-blueviolet?logo=dotnet)](https://dotnet.microsoft.com/)  
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-blue?logo=postgresql)](https://www.postgresql.org/)  
[![Redis](https://img.shields.io/badge/Redis-Cache-red?logo=redis)](https://redis.io/)  
[![Azure](https://img.shields.io/badge/Azure-App%20Service-0089D6?logo=microsoft-azure)](https://azure.microsoft.com/)  

📚 Full documentation → [See Notion Wiki](https://www.notion.so/24c209410885800f9a9cf4f0b1a83d35?v=24c20941088580c596e0000ca2b0fc14&source=copy_link)


Flight Assistant is a **cross-platform travel companion app** that helps frequent flyers:  

- Track flights with live status updates  
- Manage alliance tiers and loyalty programs  
- Discover lounge eligibility and airport benefits  

The project starts with a **.NET 10 backend**, an **iOS client (SwiftUI)**, and will expand to **Android & Web**.  

---

## ⚡ Quick Start (local dev)

**Prerequisites:** Docker, .NET 10 SDK, `az login` (for Key Vault)

```bash
# 1. Start Postgres + Redis
docker compose up -d

# 2. Run the API (connects to localhost by default)
dotnet run --project FlightAssistant.API
```

Swagger UI → http://localhost:5000/swagger

**Flight status sync is off for local runs.** `launchSettings.json` sets `FlightSync__Enabled=false` in every profile, because a local run shares the dev database with the deployed dev API, and both would poll AeroAPI for the same tracked flights and be billed twice. To test the sync itself, turn it back on for one run:

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5100 FlightSync__Enabled=true \
  dotnet run --project FlightAssistant.API --no-launch-profile
```

`--no-launch-profile` is required: a launch profile's variables override the shell's, so without it the profile's `false` wins.

**The reference-data verification job is off for local runs too.** `launchSettings.json` also sets `Verification__Enabled=false` in every profile: a local run would otherwise claim requests from the dev database's shared `verification_requests` queue alongside the deployed dev API, and write `system` verifications (audited) to its reference data. Turn it on for one run the same way, with `Verification__Enabled=true` and `--no-launch-profile`.

**OpenAPI spec:** [`spec/openapi.json`](spec/openapi.json) is generated and committed — CI fails a PR if it drifts from the code. Regenerate with `scripts/generate-openapi.sh` (from the repo root) after any route or contract change.

**HTTP Client:** Open `FlightAssistant.API/api.http` in VS Code (REST Client extension) or JetBrains Rider. Paste a B2C JWT into `@token` and run any request with one click.

---

## 🚀 Tech Stack

**Backend**
- .NET 10 LTS (ASP.NET Core Web API, Clean Architecture, MediatR, EF Core)  
- PostgreSQL (primary storage)  
- Redis (caching)  
- Azure App Service (hosting)  
- Azure AD B2C / Entra External ID (authentication)  
- Azure Key Vault (secrets)  
- Azure App Insights (telemetry)  

**Clients**
- iOS: SwiftUI (Phase 1)  
- Android: Jetpack Compose (Phase 2)  
- Web: React/Next.js (Phase 3)  

**Data Sources**
- Flight Status & Search → FlightAware AeroAPI  
- Alliances & Airports → OpenFlights + curated JSON  
- Lounge Info → curated static list  

---

## 🧩 Design Patterns

The patterns this codebase actually uses, with a representative class for each. [CLAUDE.md](CLAUDE.md) explains the reasoning behind most of them in detail.

**Architecture**

| Pattern | Where |
|---|---|
| Clean Architecture (Domain → Application → Infrastructure/API) | The four `FlightAssistant.*` projects; `Contracts` holds DTOs with no dependencies |
| CQRS | Commands in `Application/Commands`, queries in `Application/Queries`, dispatched through MediatR |
| Mediator | MediatR — endpoints send a request, never call a handler directly |
| Pipeline / cross-cutting behaviour | `ValidationBehaviour<TRequest,TResponse>` runs every FluentValidation validator before the handler |
| Dependency Inversion / Ports & Adapters | Application defines interfaces (`IFlightStatusProvider`, `ICacheService`, `IAdminContext`, …); Infrastructure implements them |
| Anti-Corruption Layer | `AeroApiFlightStatusProvider`, `AeroDataBoxFlightSearchProvider` translate provider payloads into our own result types |

**Domain-Driven Design**

| Pattern | Where |
|---|---|
| Aggregate Root | `Traveler`, `Flight` (marked with `IAggregateRoot`) |
| Entity / Value Object | `BaseEntity`, `AuditableEntity`, `ValueObject` in `Domain/Common` |
| Factory Method | Private constructors everywhere; entities are created via static `Create(...)` methods |
| Domain Events | Aggregates `Raise()` events; `DomainEventDispatchInterceptor` publishes them after commit |
| Domain Service | `LoungeEligibilityService`, `FlightSyncPlanner`, `FlightStatusApplier` |
| Guard clauses | `VerificationGuard`, `LoyaltyTierGuard` |

**Persistence**

| Pattern | Where |
|---|---|
| Repository | `FlightRepository`, `TravelerRepository`, generic `EfVerifiableRepository<TEntity>` |
| Unit of Work | EF Core `DbContext`; handlers call `SaveChangesAsync()` explicitly |
| Interceptor | `AdminAuditInterceptor` (audit in the same transaction), `DomainEventDispatchInterceptor` (post-commit) |
| Optimistic concurrency | PostgreSQL `xmin` token on `verification_requests` |
| Database-backed queue / Competing Consumers | `verification_requests` claimed with `FOR UPDATE SKIP LOCKED` |

**Behavioural & structural (GoF)**

| Pattern | Where |
|---|---|
| Strategy | `IFlightSearchProvider` and `IVerificationSource` implementations (`OurAirports…`, `Mledoze…`, `AeroApi…`) |
| Decorator | `AeroApiFlightSearchCachingDecorator` → `FlightSearchProviderSelector` → `AeroApiFlightSearchBudgetDecorator` → provider |
| Router / Selector | `FlightSearchRouter` decides the provider once; `FlightSearchProviderSelector` dispatches to it |
| Chain of Responsibility | `DelegatingHandler`s on named `HttpClient`s: `AeroApiThrottlingHandler`, `AeroDataBoxQuotaHandler`, `DataSourceObservingHandler`; ASP.NET middleware (`CorrelationIdMiddleware`, `TravelerContextMiddleware`) |
| Template Method | `HttpDataSourceProbe` base class for the health probes |
| Registry | `VerificationSourceRegistry`, `BrowsableTables`, `SensitiveProperties`, `DataSourceCatalog` |
| Builder | `AdminHealthReportBuilder`, `VerificationSummaryBuilder` |
| Observer | Domain event handlers (`FlightStatusChangedNotificationHandler`, `FlightTrackedVerificationEventHandler`); `BufferedDataSourceObserver` |
| Immutable Snapshot | `FlightSyncHeartbeat` publishes a `FlightSyncSnapshot` by atomic reference swap |

**Infrastructure & resilience**

| Pattern | Where |
|---|---|
| Options pattern | `AddOptions<T>().BindConfiguration(...).ValidateOnStart()` for every external service |
| Cache-Aside (with fallback) | `ICacheService` → `RedisCacheService`, or `MemoryCacheService` when Redis isn't configured; `AeroApiUsageCache`, `VerificationDatasetCache` |
| Negative caching | `ReferenceDataProvisioner` remembers "not found" answers (`refdata:missing:…`) |
| Retry / Circuit Breaker / Timeout | Polly via `AddStandardResilienceHandler` on the AeroAPI client |
| Throttling / Rate limiting | Outbound: `AeroApiThrottleGate`; inbound: ASP.NET rate-limiter policies |
| Budget guard / Fail-open | `ProviderBudgetGuard` holds back metered calls as spend approaches the budget |
| Background worker / Heartbeat | `FlightStatusSyncBackgroundService`, `ReferenceVerificationBackgroundService` + their heartbeats |
| Feature switches | `job_switches` and `provider_routes` tables, capped by configuration |
| Correlation ID | `CorrelationIdMiddleware` → logs, ProblemDetails `traceId`, audit rows |
| Global exception handling / Problem Details | `GlobalExceptionHandler`, `DomainException` → RFC 9457 `application/problem+json` |

---

## ✨ Features

### ✅ MVP
- Add flights manually (number + date)  
- Get **live flight status** updates  
- View **alliance tiers & benefits**  
- Lounge eligibility evaluator (tier + airline + terminal)  
- Push notifications for flight status changes (iOS / APNs)  
- Offline access with cached data  

### 🔮 Future
- Visa & entry requirements lookup  
- Auto-import flights from Gmail / Outlook  
- Sync loyalty accounts via AwardWallet  
- Higher-reliability APIs (FlightAware, Cirium)  
- Android & Web clients  
- Expanded lounge & airport database  
