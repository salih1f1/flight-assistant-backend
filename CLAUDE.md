# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build --configuration Release

# Run API
dotnet run --project FlightAssistant.API

# Run all tests
dotnet test

# Run tests in a specific project
dotnet test FlightAssistant.Domain.Tests.csproj

# Run a specific test class
dotnet test FlightAssistant.Domain.Tests.csproj --filter "ClassName=FlightTests"

# Run a specific test by name
dotnet test FlightAssistant.Domain.Tests.csproj --filter "Name~Create_NormalizesFlightNumber"

# EF Core migrations
dotnet ef migrations add <MigrationName> --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
dotnet ef database update --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
```

## Architecture

This is an ASP.NET Core Web API using **Clean Architecture + CQRS** (via MediatR) with **Domain-Driven Design**.

**Layer responsibilities:**
- `FlightAssistant.Domain` — Aggregate roots, entities, domain events, value objects, exceptions. Entities use factory methods (private constructors). Domain events are raised inside aggregates for side effects.
- `FlightAssistant.Application` — MediatR command/query handlers, FluentValidation validators, pipeline behaviors (validation, logging). All use cases live here.
- `FlightAssistant.Infrastructure` — EF Core + PostgreSQL, Redis caching, HTTP clients for external APIs, Apple Push Notifications (dotAPNS), repository implementations.
- `FlightAssistant.API` — Minimal API endpoints, middleware, DI registration in `Program.cs`, hosted services, seed data.
- `FlightAssistant.Contracts` — DTOs for all API requests and responses.
- `FlightAssistant.SharedKernel` — Base classes shared across layers (e.g., `AggregateRoot`, `DomainEvent`).

**Bounded contexts:** Travelers, Flights, Loyalty, Lounges, Visa, Configuration (reference data).

**Key patterns:**
- Commands are idempotent — handlers check for existing state before creating.
- Domain events drive side effects (e.g., `TravelerRegistered` triggers push notification setup).
- `RefDataRepository` consolidates all reference data reads (airports, airlines, countries, alliances); see docs in that file for DDD rationale.
- The `Configuration` bounded context is intentionally kept as a single repository for now and is documented to be split later.

## Tech Stack

- **.NET 8**, ASP.NET Core Minimal APIs, PostgreSQL 16, Redis
- **Auth:** Azure AD B2C / Entra External ID (JWT Bearer)
- **Cloud:** Azure App Service, Key Vault, Application Insights
- **External APIs:** AeroDataBox, AviationStack, Sherpa (visa), OpenFlights (reference data)
- **Push notifications:** APNs via dotAPNS (iOS only currently)
- **CI/CD:** GitHub Actions → Azure Web App (triggered on push to `development`), EF migration bundles applied automatically

## Branching

All new features and bugfixes must be developed in a dedicated branch. Never commit directly to `development` or `main`. Branch off from `development`, and open a PR back into `development` when ready.

## Tests

Tests are unit tests focused on domain entity behavior (`FlightAssistant.Domain.Tests`). Domain entities use private constructors, so tests go through factory methods (e.g., `Flight.Create(...)`). Integration tests for infrastructure/application layers are not yet in place.
