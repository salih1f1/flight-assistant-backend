# Codebase Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix all Critical and Important issues identified in the 2026-05-17 full codebase review of `flight-assistant-backend`.

**Architecture:** ASP.NET Core Web API (.NET 10), Clean Architecture + CQRS (MediatR), DDD. Fixes follow existing patterns — `DomainException(message, statusCode)` for error handling, FluentValidation for input validation, private constructors + factory methods for domain construction. Each phase produces independently deployable improvements.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core 10, PostgreSQL 16, MediatR v13, FluentValidation 12, NSubstitute (tests), xUnit 2.9, CsvHelper 33

**GitHub Issues:** #189–#205 on `salih1f1/flight-assistant-backend`

**Build:** `dotnet build --configuration Release`
**Tests:** `dotnet test`

---

## Phase 1 — Critical Security Bugs

### Task 1: Add flight ownership check to status and sync endpoints (GitHub #189)

**Files:**
- Modify: `FlightAssistant.Application/Queries/Flights/GetFlightStatusQuery.cs`
- Modify: `FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs`
- Modify: `FlightAssistant.API/Endpoints/FlightEndpoints.cs`
- Create: `FlightAssistant.Application.Tests/Queries/Flights/GetFlightStatusQueryHandlerTests.cs`

- [ ] **Step 1: Write the failing test for GetFlightStatusQueryHandler**

```csharp
// FlightAssistant.Application.Tests/Queries/Flights/GetFlightStatusQueryHandlerTests.cs
public class GetFlightStatusQueryHandlerTests
{
    private readonly IFlightRepository _flights = Substitute.For<IFlightRepository>();
    private readonly GetFlightStatusQueryHandler _handler;

    public GetFlightStatusQueryHandlerTests()
        => _handler = new GetFlightStatusQueryHandler(_flights);

    [Fact]
    public async Task Handle_TravelerNotTrackingFlight_ThrowsDomainException403()
    {
        _flights.GetTravelerFlightAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((TravelerFlight?)null);

        var query = new GetFlightStatusQuery(TravelerId: Guid.NewGuid(), FlightId: 1);

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => _handler.Handle(query, CancellationToken.None));
        Assert.Equal(403, ex.StatusCode);
    }
}
```

- [ ] **Step 2: Run test — expect FAIL (GetFlightStatusQuery has no TravelerId yet)**

```bash
dotnet test FlightAssistant.Application.Tests --filter "GetFlightStatusQueryHandlerTests"
```

- [ ] **Step 3: Add TravelerId to GetFlightStatusQuery and enforce ownership in handler**

```csharp
// GetFlightStatusQuery.cs — update the query record:
public record GetFlightStatusQuery(Guid TravelerId, int FlightId) : IRequest<IReadOnlyList<FlightStatusEventDto>>;

// In GetFlightStatusQueryHandler.Handle:
var link = await _flights.GetTravelerFlightAsync(query.TravelerId, query.FlightId, ct);
if (link is null)
    throw new DomainException("Not tracking this flight.", 403);
// ... then proceed with existing status event fetch
```

- [ ] **Step 4: Add TravelerId to SyncFlightStatusCommand and enforce ownership in handler**

```csharp
// SyncFlightStatusCommand.cs — update the command record:
public record SyncFlightStatusCommand(Guid TravelerId, int FlightId) : IRequest<bool>;

// In SyncFlightStatusCommandHandler.Handle — add before fetching flight:
var link = await _flights.GetTravelerFlightAsync(cmd.TravelerId, cmd.FlightId, ct);
if (link is null)
    throw new DomainException("Not tracking this flight.", 403);
```

- [ ] **Step 5: Update endpoints to pass TravelerId**

```csharp
// FlightEndpoints.cs — GET /flights/{flightId}/status:
var travelerId = http.User.GetTravelerId();
var result = await mediator.Send(new GetFlightStatusQuery(travelerId, flightId), ct);

// FlightEndpoints.cs — POST /flights/{flightId}/sync:
var travelerId = http.User.GetTravelerId();
var result = await mediator.Send(new SyncFlightStatusCommand(travelerId, flightId), ct);
```

- [ ] **Step 6: Run tests**

```bash
dotnet test
```
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add FlightAssistant.Application/Queries/Flights/GetFlightStatusQuery.cs \
        FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs \
        FlightAssistant.API/Endpoints/FlightEndpoints.cs \
        FlightAssistant.Application.Tests/Queries/Flights/GetFlightStatusQueryHandlerTests.cs
git commit -m "fix(security): add flight ownership check to status and sync endpoints (#189)"
```

---

### Task 2: Fix X-Correlation-Id header injection risk (GitHub security finding)

**Files:**
- Modify: `FlightAssistant.API/Middleware/CorrelationIdMiddleware.cs`

- [ ] **Step 1: Add sanitization to CorrelationIdMiddleware**

```csharp
// CorrelationIdMiddleware.cs — replace the raw header read:
var rawId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault();
var correlationId = IsValidCorrelationId(rawId)
    ? rawId!
    : Guid.NewGuid().ToString("N");

// Add the helper method:
private static bool IsValidCorrelationId(string? value)
    => value is { Length: > 0 and <= 64 } &&
       value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
```

- [ ] **Step 2: Run tests and build**

```bash
dotnet build --configuration Release && dotnet test
```

- [ ] **Step 3: Commit**

```bash
git add FlightAssistant.API/Middleware/CorrelationIdMiddleware.cs
git commit -m "fix(security): sanitize X-Correlation-Id header to prevent injection"
```

---

### Task 3: Scope CORS policy to admin group only (GitHub security finding)

**Files:**
- Modify: `FlightAssistant.API/Program.cs`

- [ ] **Step 1: Remove global UseCors, apply to admin group only**

Find `app.UseCors("AdminPanel");` (line ~292) and remove it.

In the admin group definition, add `.RequireCors("AdminPanel")`:

```csharp
var adminGroup = app.MapGroup("admin")
    .RequireAuthorization("AdminOnly")
    .RequireCors("AdminPanel");   // ← add this
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build --configuration Release
```

- [ ] **Step 3: Commit**

```bash
git add FlightAssistant.API/Program.cs
git commit -m "fix(security): scope AdminPanel CORS policy to admin group only"
```

---

### Task 4: Add DisplayName MaximumLength validator (GitHub #191 adjacent)

**Files:**
- Modify: `FlightAssistant.Application/Validators/RegisterTravelerCommandValidator.cs`
- Modify: `FlightAssistant.Application/Validators/UpdateTravelerProfileCommandValidator.cs`

- [ ] **Step 1: Add validation rules**

```csharp
// RegisterTravelerCommandValidator — add:
RuleFor(x => x.DisplayName)
    .MaximumLength(100)
    .When(x => x.DisplayName is not null);

// UpdateTravelerProfileCommandValidator — add:
RuleFor(x => x.DisplayName)
    .MaximumLength(100)
    .When(x => x.DisplayName is not null);
```

- [ ] **Step 2: Run tests**

```bash
dotnet test
```

- [ ] **Step 3: Commit**

```bash
git add FlightAssistant.Application/Validators/RegisterTravelerCommandValidator.cs \
        FlightAssistant.Application/Validators/UpdateTravelerProfileCommandValidator.cs
git commit -m "fix(validation): add MaximumLength(100) for DisplayName field"
```

---

## Phase 2 — Critical Correctness Bugs

### Task 5: Fix exception handling gaps — replace framework exceptions with DomainException (GitHub #191)

**Files:**
- Modify: `FlightAssistant.Application/Commands/Travelers/AddDeviceTokenCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Travelers/UpdateTravelerProfileCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Travelers/RemoveDeviceTokenCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Loyalty/UpdateLoyaltyMembershipCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Loyalty/RemoveLoyaltyMembershipCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Airlines/UpdateAirlineCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Airports/UpdateAirportCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Lounges/UpdateLoungeCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/LoyaltyPrograms/UpdateLoyaltyProgramCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Flights/ToggleFlightNotificationCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Flights/TrackFlightCommand.cs`

- [ ] **Step 1: Replace InvalidOperationException with DomainException(msg, 404)**

In each handler listed above, find `throw new InvalidOperationException("...")` and replace:

```csharp
// Before:
throw new InvalidOperationException("Traveler not found.");
throw new InvalidOperationException("Airline with id {id} not found.");

// After:
throw new DomainException("Traveler not found.", 404);
throw new DomainException($"Airline with id {id} not found.", 404);
```

- [ ] **Step 2: Replace KeyNotFoundException with DomainException(msg, 404)**

```csharp
// Before:
throw new KeyNotFoundException($"Airline {cmd.Id} not found.");

// After:
throw new DomainException($"Airline {cmd.Id} not found.", 404);
```

- [ ] **Step 3: Replace UnauthorizedAccessException with DomainException(msg, 403)**

```csharp
// UpdateLoyaltyMembershipCommand.cs:
throw new DomainException("Cannot update another traveler's membership.", 403);

// RemoveLoyaltyMembershipCommand.cs:
throw new DomainException("Cannot remove another traveler's membership.", 403);
```

- [ ] **Step 4: Fix DeviceTokenNotFoundException dead code (GitHub #191 adjacent)**

```csharp
// RemoveDeviceTokenCommand.cs — replace:
throw new InvalidOperationException("Device token not found.");
// With:
throw new DomainException("Device token not found.", 404);
```

`DeviceTokenNotFoundException.cs` can be deleted (it was never thrown).

- [ ] **Step 5: Run tests**

```bash
dotnet test
```
Update any test that was asserting `InvalidOperationException`/`KeyNotFoundException` to assert `DomainException` instead.

- [ ] **Step 6: Commit**

```bash
git add -u
git commit -m "fix: replace framework exceptions with DomainException in Application handlers (#191)"
```

---

### Task 6: Fix background sync filter — all tracked flights must be polled (GitHub #192)

**Files:**
- Modify: `FlightAssistant.Infrastructure/Persistance/Repositories/FlightRepository.cs`

- [ ] **Step 1: Write a test documenting the correct behaviour**

In `FlightAssistant.Application.Tests` (or integration tests), add a test asserting that `GetActiveTrackedFlightsAsync` returns flights even when all tracking records have `Notify = false`.

- [ ] **Step 2: Remove the Notify filter**

```csharp
// FlightRepository.cs — GetActiveTrackedFlightsAsync:
// Remove:
_db.TravelerFlights.Any(tf => tf.FlightId == f.Id && tf.Notify)
// Replace with:
_db.TravelerFlights.Any(tf => tf.FlightId == f.Id)
```

- [ ] **Step 3: Run tests**

```bash
dotnet test
```

- [ ] **Step 4: Commit**

```bash
git add FlightAssistant.Infrastructure/Persistance/Repositories/FlightRepository.cs
git commit -m "fix: poll all tracked flights regardless of Notify flag (#192)"
```

---

### Task 7: Fix Alliance verify endpoint — string PK vs int FindByIdAsync mismatch (GitHub #193)

**Files:**
- Modify: `FlightAssistant.Domain/ReferenceData/Entities/Alliance.cs`
- Modify: `FlightAssistant.Infrastructure/Persistance/Configurations/ReferenceData/AllianceConfiguration.cs`
- Create: EF Core migration

- [ ] **Step 1: Add surrogate int Id to Alliance**

```csharp
// Alliance.cs — add:
public int Id { get; private set; }
```

- [ ] **Step 2: Update AllianceConfiguration**

```csharp
// AllianceConfiguration.cs:
b.HasKey(x => x.Id);
b.Property(x => x.Id).ValueGeneratedOnAdd();
b.HasIndex(x => x.Code).IsUnique();
// Remove: b.HasKey(x => x.Code);
```

- [ ] **Step 3: Create EF migration**

```bash
dotnet ef migrations add AddAllianceSurrogateId \
  --project FlightAssistant.Infrastructure \
  --startup-project FlightAssistant.API
```

Review the generated migration — it should add an `id` column to the `alliances` table.

- [ ] **Step 4: Run tests**

```bash
dotnet test
```

- [ ] **Step 5: Commit**

```bash
git add FlightAssistant.Domain/ReferenceData/Entities/Alliance.cs \
        FlightAssistant.Infrastructure/Persistance/Configurations/ReferenceData/AllianceConfiguration.cs \
        FlightAssistant.Infrastructure/Migrations/
git commit -m "fix: add surrogate int Id to Alliance to fix EfVerifiableRepository lookup (#193)"
```

---

### Task 8: Fix CompositeFlightStatusProvider.ProviderName (GitHub #197)

**Files:**
- Modify: `FlightAssistant.Infrastructure/ExternalServices/FlightStatus/CompositeFlightStatusProvider.cs`

- [ ] **Step 1: Track which provider responded**

```csharp
// CompositeFlightStatusProvider.cs:
private string _lastUsedProviderName;

public CompositeFlightStatusProvider(IFlightStatusProvider primary, IFlightStatusProvider fallback, ILogger<CompositeFlightStatusProvider> logger)
{
    _primary = primary;
    _fallback = fallback;
    _logger = logger;
    _lastUsedProviderName = primary.ProviderName;
}

public string ProviderName => _lastUsedProviderName;

public async Task<FlightStatusResult?> GetStatusAsync(string flightNumber, DateOnly date, CancellationToken ct)
{
    try
    {
        var result = await _primary.GetStatusAsync(flightNumber, date, ct);
        if (result is not null)
        {
            _lastUsedProviderName = _primary.ProviderName;
            return result;
        }
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Primary flight status provider failed, trying fallback");
    }

    var fallbackResult = await _fallback.GetStatusAsync(flightNumber, date, ct);
    _lastUsedProviderName = _fallback.ProviderName;
    return fallbackResult;
}
```

- [ ] **Step 2: Add test for fallback provider name**

In `SyncFlightStatusCommandHandlerTests.cs`, add:

```csharp
[Fact]
public async Task Handle_PrimaryThrows_FallbackResultHasCorrectSource()
{
    // Arrange: primary throws, fallback returns a result
    // Assert: FlightStatusEvent.Source == "AviationStack" (not "AeroDataBox")
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test
```

- [ ] **Step 4: Commit**

```bash
git add FlightAssistant.Infrastructure/ExternalServices/FlightStatus/CompositeFlightStatusProvider.cs
git commit -m "fix: CompositeFlightStatusProvider.ProviderName reflects actual provider used (#197)"
```

---

## Phase 3 — Domain Model Integrity

### Task 9: Introduce BaseIntEntity and have Flight inherit it (GitHub #194)

**Files:**
- Create: `FlightAssistant.Domain/Common/BaseIntEntity.cs`
- Modify: `FlightAssistant.Domain/Flights/Entities/Flight.cs`

- [ ] **Step 1: Create BaseIntEntity**

```csharp
// FlightAssistant.Domain/Common/BaseIntEntity.cs
namespace FlightAssistant.Domain.Common;

public abstract class BaseIntEntity : IHasDomainEvents
{
    private readonly List<DomainEvent> _domainEvents = new();

    public int Id { get; private set; }

    public IReadOnlyList<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(DomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();

    public override bool Equals(object? obj)
    {
        if (obj is not BaseIntEntity other) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id && GetType() == other.GetType();
    }

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
```

- [ ] **Step 2: Update Flight to inherit BaseIntEntity**

```csharp
// Flight.cs — change:
public sealed class Flight : IAggregateRoot, IHasDomainEvents
// To:
public sealed class Flight : BaseIntEntity, IAggregateRoot

// Remove the duplicated fields:
// DELETE: private readonly List<DomainEvent> _domainEvents = new();
// DELETE: public IReadOnlyList<DomainEvent> DomainEvents => ...
// DELETE: public void ClearDomainEvents() => ...
// DELETE: protected void Raise(DomainEvent domainEvent) => ...
// DELETE: public int Id { get; private set; }  (now inherited)
```

- [ ] **Step 3: Run tests**

```bash
dotnet test FlightAssistant.Domain.Tests
dotnet test FlightAssistant.Application.Tests
```

- [ ] **Step 4: Commit**

```bash
git add FlightAssistant.Domain/Common/BaseIntEntity.cs \
        FlightAssistant.Domain/Flights/Entities/Flight.cs
git commit -m "refactor(domain): introduce BaseIntEntity; Flight now inherits shared event infrastructure (#194)"
```

---

### Task 10: Add private constructors to Lounge and SeedableEntity subclasses (GitHub #195)

**Files:**
- Modify: `FlightAssistant.Domain/Lounges/Entities/Lounge.cs`
- Modify: `FlightAssistant.Domain/Common/SeedableEntity.cs`
- Modify: `FlightAssistant.Domain/ReferenceData/Entities/Airport.cs`
- Modify: `FlightAssistant.Domain/ReferenceData/Entities/Airline.cs`
- Modify: `FlightAssistant.Domain/ReferenceData/Entities/Alliance.cs`
- Modify: `FlightAssistant.Domain/ReferenceData/Entities/Country.cs`
- Modify: `FlightAssistant.Domain/Common/Interfaces/ISeedableEntity.cs`

- [ ] **Step 1: Add private constructor to Lounge**

```csharp
// Lounge.cs — add inside the class:
private Lounge() { } // EF
```

- [ ] **Step 2: Add protected constructor to SeedableEntity, private to subclasses**

```csharp
// SeedableEntity.cs — add:
protected SeedableEntity() { } // EF

// Airport.cs — add:
private Airport() { } // EF

// Airline.cs — add:
private Airline() { } // EF

// Alliance.cs — add:
private Alliance() { } // EF

// Country.cs — add:
private Country() { } // EF
```

- [ ] **Step 3: Remove public setters from ISeedableEntity**

```csharp
// ISeedableEntity.cs — change:
DateTime FirstSeenAt { get; set; }
DateTime LastSeenAt { get; set; }
// To:
DateTime FirstSeenAt { get; }
DateTime LastSeenAt { get; }
```

Update `SeedableEntity.cs` to use `protected set` for EF:

```csharp
public DateTime FirstSeenAt { get; protected set; } = DateTime.UtcNow;
public DateTime LastSeenAt { get; protected set; } = DateTime.UtcNow;
```

- [ ] **Step 4: Run tests**

```bash
dotnet test
```

If EF Core materialization fails in tests, check that the protected constructor on `SeedableEntity` is accessible to EF Core via inheritance chain.

- [ ] **Step 5: Commit**

```bash
git add -u
git commit -m "fix(domain): add private constructors to Lounge and SeedableEntity subclasses (#195)"
```

---

### Task 11: Add ReplaceAllProviders/ReplaceAllTiers and fix collection encapsulation (GitHub #196)

**Files:**
- Modify: `FlightAssistant.Domain/Lounges/Entities/Lounge.cs`
- Modify: `FlightAssistant.Domain/Loyalty/Entities/LoyaltyProgram.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Import/ImportLoungesCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Import/ImportLoyaltyProgramsCommand.cs`

- [ ] **Step 1: Add ReplaceAllProviders to Lounge**

```csharp
// Lounge.cs — add method:
public void ReplaceAllProviders(IEnumerable<LoungeProviderLink> newProviders)
{
    _providers.Clear();
    foreach (var p in newProviders)
        _providers.Add(p);
}
// Ensure _providers is the backing List<LoungeProviderLink> field
```

- [ ] **Step 2: Add ReplaceAllTiers to LoyaltyProgram**

```csharp
// LoyaltyProgram.cs — add method:
public void ReplaceAllTiers(IEnumerable<LoyaltyTier> newTiers)
{
    _tiers.Clear();
    foreach (var t in newTiers)
        _tiers.Add(t);
}
```

- [ ] **Step 3: Update import handlers to use domain methods**

```csharp
// ImportLoungesCommand.cs — replace:
existing.Providers.Clear();
foreach (var link in providerLinks) existing.Providers.Add(link);
// With:
existing.ReplaceAllProviders(providerLinks);

// ImportLoyaltyProgramsCommand.cs — replace:
existing.Tiers.Clear();
foreach (var tier in tiers) existing.Tiers.Add(tier);
// With:
existing.ReplaceAllTiers(tiers);
```

- [ ] **Step 4: Verify EF Core orphan deletion**

Check `LoyaltyTierConfiguration.cs` and `LoungeProviderLinkConfiguration.cs` for:
```csharp
.OnDelete(DeleteBehavior.ClientCascade)
```
If not present, add it and create an EF migration. Without it, orphan rows will accumulate on re-import.

- [ ] **Step 5: Run tests**

```bash
dotnet test
```

- [ ] **Step 6: Commit**

```bash
git add -u
git commit -m "refactor(domain): encapsulate provider/tier collection via ReplaceAll methods (#196)"
```

---

## Phase 4 — CSV Import/Export Quality

### Task 12: Add file size limit, content-type validation, and map CsvHelperException to 400 (GitHub #199)

**Files:**
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminImportEndpoints.cs`
- Modify: `FlightAssistant.API/Middleware/GlobalExceptionHandler.cs`

- [ ] **Step 1: Add content-type and size guards to all 8 import endpoints**

At the start of each of the 8 import handler lambdas (alliances, countries, airports, airlines, lounges, lounge-providers, loyalty-programs, loyalty-program-kinds):

```csharp
if (!file.ContentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase))
    return Results.BadRequest("Only text/csv files are accepted.");
if (file.Length > 10 * 1024 * 1024)
    return Results.BadRequest("File exceeds the 10 MB limit.");

await using var stream = file.OpenReadStream();
// pass stream to parser instead of file.OpenReadStream() inline
```

- [ ] **Step 2: Map CsvHelperException in GlobalExceptionHandler**

```csharp
// GlobalExceptionHandler.cs — add before the default case:
case CsvHelper.CsvHelperException csvEx:
    context.Response.StatusCode = StatusCodes.Status400BadRequest;
    await context.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = 400,
        Title = "Invalid CSV file",
        Detail = csvEx.Message,
        Type = "https://tools.ietf.org/html/rfc7807"
    }, cancellationToken: ct);
    return;
```

Add `using CsvHelper;` at the top of `GlobalExceptionHandler.cs`.

- [ ] **Step 3: Run tests**

```bash
dotnet test
```

- [ ] **Step 4: Commit**

```bash
git add FlightAssistant.API/Endpoints/Admin/AdminImportEndpoints.cs \
        FlightAssistant.API/Middleware/GlobalExceptionHandler.cs
git commit -m "fix(csv): add file size/type validation and map CsvHelperException to 400 (#199)"
```

---

### Task 13: Return HTTP 207 for partial import errors + add Produces annotations (GitHub #200)

**Files:**
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminImportEndpoints.cs`

- [ ] **Step 1: Change response based on error count**

For each of the 8 import endpoint lambdas, change the return statement:

```csharp
// Before:
return Results.Ok(result);

// After:
return result.Errors.Count == 0
    ? Results.Ok(result)
    : Results.Json(result, statusCode: 207);
```

- [ ] **Step 2: Add Produces annotations**

After `.WithSummary(...)` on each endpoint, add:

```csharp
.Produces<ImportResult>(200)
.Produces<ImportResult>(207)
.ProducesValidationProblem()
.ProducesProblem(400)
```

- [ ] **Step 3: Run tests**

```bash
dotnet test
```

- [ ] **Step 4: Commit**

```bash
git add FlightAssistant.API/Endpoints/Admin/AdminImportEndpoints.cs
git commit -m "fix(csv): return 207 Multi-Status on partial import errors; add Produces annotations (#200)"
```

---

### Task 14: Add missing MaximumLength validators for lounge import fields (GitHub #199 adjacent)

**Files:**
- Modify: `FlightAssistant.Application/Commands/Admin/Import/ImportLoungesCommand.cs`

- [ ] **Step 1: Add missing length rules to the lounge import validator**

In `ImportLoungesCommandValidator` inside the `ChildRules` block for each lounge item:

```csharp
item.RuleFor(d => d.Terminal)
    .MaximumLength(32)
    .When(d => d.Terminal is not null);
item.RuleFor(d => d.LocationHint)
    .MaximumLength(200)
    .When(d => d.LocationHint is not null);
item.RuleFor(d => d.Hours)
    .MaximumLength(200)
    .When(d => d.Hours is not null);
```

- [ ] **Step 2: Run tests**

```bash
dotnet test
```

- [ ] **Step 3: Commit**

```bash
git add FlightAssistant.Application/Commands/Admin/Import/ImportLoungesCommand.cs
git commit -m "fix(validation): add MaximumLength validators for lounge Terminal/LocationHint/Hours fields"
```

---

## Phase 5 — Performance (N+1 Queries)

### Task 15: Fix N+1 in GetTravelerFlightsQueryHandler (GitHub #202)

**Files:**
- Modify: `FlightAssistant.Application/Queries/Flights/GetTravelerFlightsQuery.cs`

- [ ] **Step 1: Replace per-row lookups with batch map calls**

`IRefDataRepository` already has `GetAirlineIataMapAsync` and `GetAirportIataMapAsync`. Use them:

```csharp
// GetTravelerFlightsQueryHandler.Handle — replace the per-row IATA lookups:
var airlineIds = rows.Select(r => r.Flight.AirlineId).Distinct().ToList();
var airportIds = rows
    .SelectMany(r => new[] { r.Flight.DepartureAirportId, r.Flight.ArrivalAirportId })
    .Distinct().ToList();

var airlineMap = await _refData.GetAirlineIataMapAsync(airlineIds, ct);
var airportMap = await _refData.GetAirportIataMapAsync(airportIds, ct);

var items = rows.Select(r => new TrackedFlightDto(
    // use airlineMap.GetValueOrDefault(r.Flight.AirlineId, "") instead of per-row call
    // use airportMap.GetValueOrDefault(r.Flight.DepartureAirportId, "") etc.
)).ToList();
```

- [ ] **Step 2: Update handler test to assert batch calls**

In `GetTravelerFlightsQueryHandlerTests.cs`, verify `GetAirlineIataMapAsync` and `GetAirportIataMapAsync` are called once each (not per row).

- [ ] **Step 3: Run tests**

```bash
dotnet test
```

- [ ] **Step 4: Commit**

```bash
git add FlightAssistant.Application/Queries/Flights/GetTravelerFlightsQuery.cs
git commit -m "perf: eliminate N+1 in GetTravelerFlightsQuery using batch IATA map lookups (#202)"
```

---

### Task 16: Fix N+1 in FlightStatusChangedNotificationHandler (GitHub #202)

**Files:**
- Modify: `FlightAssistant.Domain/Travelers/Interfaces/ITravelerRepository.cs`
- Modify: `FlightAssistant.Infrastructure/Persistance/Repositories/TravelerRepository.cs`
- Modify: `FlightAssistant.Application/EventHandlers/FlightStatusSyncedEventHandler.cs`

- [ ] **Step 1: Add GetByIdsAsync to ITravelerRepository**

```csharp
// ITravelerRepository.cs — add:
Task<IReadOnlyList<Traveler>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
```

- [ ] **Step 2: Implement in TravelerRepository**

```csharp
// TravelerRepository.cs — implement:
public async Task<IReadOnlyList<Traveler>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    => await _db.Travelers
        .Where(t => ids.Contains(t.Id))
        .ToListAsync(ct);
```

- [ ] **Step 3: Update event handler to use batch load**

```csharp
// FlightStatusSyncedEventHandler.cs:
var travelerIds = trackings.Select(tf => tf.TravelerId).ToList();
var travelers = await _travelers.GetByIdsAsync(travelerIds, ct);
var travelerMap = travelers.ToDictionary(t => t.Id);

foreach (var tracking in trackings.Where(tf => tf.Notify))
{
    if (!travelerMap.TryGetValue(tracking.TravelerId, out var traveler)) continue;
    // send APNs notification
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test
```

- [ ] **Step 5: Commit**

```bash
git add FlightAssistant.Domain/Travelers/Interfaces/ITravelerRepository.cs \
        FlightAssistant.Infrastructure/Persistance/Repositories/TravelerRepository.cs \
        FlightAssistant.Application/EventHandlers/FlightStatusSyncedEventHandler.cs
git commit -m "perf: batch-load travelers in FlightStatusChangedNotificationHandler (#202)"
```

---

## Phase 6 — Migration Cleanup

### Task 17: Fix package versions after .NET 10 migration (GitHub #201)

**Files:**
- Modify: `FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj`
- Modify: `FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj`
- Modify: `FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj`
- Modify: `FlightAssistant.API/FlightAssistant.API.csproj`
- Modify: `AirportsExtractor/AirportsExtractor.csproj`
- Modify: `AirlinesExtractor/AirlinesExtractor.csproj`
- Create: `global.json`

- [ ] **Step 1: Fix coverlet.collector in all 3 test projects**

```xml
<!-- All three test .csproj files — change: -->
<PackageReference Include="coverlet.collector" Version="10.0.0" />
<!-- To: -->
<PackageReference Include="coverlet.collector" Version="6.0.4" />
```

- [ ] **Step 2: Fix ApplicationInsights version**

```xml
<!-- FlightAssistant.API.csproj — change: -->
<PackageReference Include="Microsoft.ApplicationInsights.AspNetCore" Version="3.1.1" />
<!-- To: -->
<PackageReference Include="Microsoft.ApplicationInsights.AspNetCore" Version="2.23.0" />
```

- [ ] **Step 3: Fix HealthChecks.Redis version**

```xml
<!-- FlightAssistant.API.csproj — change: -->
<PackageReference Include="AspNetCore.HealthChecks.Redis" Version="9.0.0" />
<!-- To: -->
<PackageReference Include="AspNetCore.HealthChecks.Redis" Version="10.0.0" />
```

- [ ] **Step 4: Update extractor projects to net10.0**

```xml
<!-- AirportsExtractor.csproj and AirlinesExtractor.csproj — change: -->
<TargetFramework>net8.0</TargetFramework>
<!-- To: -->
<TargetFramework>net10.0</TargetFramework>
```

- [ ] **Step 5: Create global.json**

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestMinor"
  }
}
```

Save to repo root: `/global.json`

- [ ] **Step 6: Verify clean restore**

```bash
dotnet nuget locals all --clear
dotnet restore
dotnet build --configuration Release
dotnet test
```
Expected: all pass, no package resolution warnings.

- [ ] **Step 7: Commit**

```bash
git add -u global.json
git commit -m "fix(deps): correct package versions after .NET 10 migration (#201)"
```

---

### Task 18: Fix Domain.Tests referencing API project (GitHub #201 adjacent)

**Files:**
- Modify: `FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj`
- Move: `FlightAssistant.Domain.Tests/Authentication/ClaimsPrincipalExtensionsTests.cs` → `FlightAssistant.API.Tests/Authentication/ClaimsPrincipalExtensionsTests.cs`

- [ ] **Step 1: Move the test file**

```bash
mkdir -p FlightAssistant.API.Tests/Authentication
mv FlightAssistant.Domain.Tests/Authentication/ClaimsPrincipalExtensionsTests.cs \
   FlightAssistant.API.Tests/Authentication/ClaimsPrincipalExtensionsTests.cs
```

Update the namespace in the moved file to match `FlightAssistant.API.Tests`.

- [ ] **Step 2: Remove API project reference from Domain.Tests**

```xml
<!-- FlightAssistant.Domain.Tests.csproj — remove: -->
<ProjectReference Include="..\FlightAssistant.API\FlightAssistant.API.csproj" />
```

- [ ] **Step 3: Run tests**

```bash
dotnet test
```
Expected: `ClaimsPrincipalExtensionsTests` now runs in `FlightAssistant.API.Tests`.

- [ ] **Step 4: Commit**

```bash
git add -u
git commit -m "fix: move ClaimsPrincipalExtensionsTests to API.Tests to fix layer violation"
```

---

## Phase 7 — Test Coverage Gaps

### Task 19: Fix LoungeEligibilityService unknown-alliance fallback (GitHub #204)

**Files:**
- Modify: `FlightAssistant.Domain/Lounges/Services/LoungeEligibilityService.cs`
- Modify: `FlightAssistant.Domain.Tests/Lounges/LoungeEligibilityTests.cs`

- [ ] **Step 1: Write failing tests for unknown alliance and SA/ST tiers**

```csharp
// LoungeEligibilityTests.cs — add:
[Fact]
public void IsEligible_UnknownAlliance_ReturnsFalse()
{
    // Set up lounge requiring alliance "XX" minTier "GOLD"
    // Assert result is false
}

[Theory]
[InlineData("SA", "SILVER", "SILVER", true)]
[InlineData("SA", "SILVER", "GOLD", false)]
[InlineData("SA", "GOLD", "SILVER", true)]
[InlineData("ST", "ELITE", "ELITE", true)]
[InlineData("ST", "ELITE", "ELITE_PLUS", false)]
[InlineData("ST", "ELITE_PLUS", "ELITE", true)]
public void IsEligible_AllianceTierComparisons(string alliance, string memberTier, string minTier, bool expected)
{
    // ...
}
```

- [ ] **Step 2: Run — expect FAIL (unknown alliance currently returns true)**

```bash
dotnet test FlightAssistant.Domain.Tests --filter "LoungeEligibilityTests"
```

- [ ] **Step 3: Fix the fallback in LoungeEligibilityService**

```csharp
// LoungeEligibilityService.cs line 98 — change:
if (!AllianceTierRanks.TryGetValue(allianceCode, out var ranks))
    return true;
// To:
if (!AllianceTierRanks.TryGetValue(allianceCode, out var ranks))
    return false;
```

- [ ] **Step 4: Run tests — expect PASS**

```bash
dotnet test FlightAssistant.Domain.Tests --filter "LoungeEligibilityTests"
```

- [ ] **Step 5: Commit**

```bash
git add FlightAssistant.Domain/Lounges/Services/LoungeEligibilityService.cs \
        FlightAssistant.Domain.Tests/Lounges/LoungeEligibilityTests.cs
git commit -m "fix(domain): unknown alliance falls back to ineligible, not eligible (#204)"
```

---

### Task 20: Add missing handler tests — priority handlers (GitHub #203)

**Files:**
- Create: `FlightAssistant.Application.Tests/Queries/Lounges/GetLoungesAtAirportQueryHandlerTests.cs`
- Create: `FlightAssistant.Application.Tests/Commands/Flights/UntrackFlightCommandHandlerTests.cs`
- Create: `FlightAssistant.Application.Tests/Commands/Flights/ToggleFlightNotificationCommandHandlerTests.cs`
- Create: `FlightAssistant.Application.Tests/Commands/Travelers/RemoveDeviceTokenCommandHandlerTests.cs`

- [ ] **Step 1: Add GetLoungesAtAirportQueryHandler tests**

Test both code paths:
1. `TravelerId = null` — no membership lookup, only public lounge data returned
2. `TravelerId != null` — membership lookup done, eligibility computed

```csharp
// GetLoungesAtAirportQueryHandlerTests.cs
[Fact]
public async Task Handle_NullTravelerId_ReturnsLoungesWithoutEligibility()
{
    // Assert: _loyalty.GetMembershipsForTravelerAsync never called
}

[Fact]
public async Task Handle_WithTravelerId_ComputesEligibility()
{
    // Assert: _loyalty.GetMembershipsForTravelerAsync called once
    // Assert: result contains eligibility information
}
```

- [ ] **Step 2: Add UntrackFlightCommandHandler tests**

```csharp
// UntrackFlightCommandHandlerTests.cs
[Fact]
public async Task Handle_ExistingTracking_RemovesAndSaves() { ... }

[Fact]
public async Task Handle_NotTracking_CompletesGracefully() { ... } // idempotent
```

- [ ] **Step 3: Add ToggleFlightNotificationCommandHandler tests**

```csharp
[Fact]
public async Task Handle_FlightFound_TogglesNotify() { ... }

[Fact]
public async Task Handle_FlightNotFound_ThrowsDomainException() { ... }
```

- [ ] **Step 4: Run tests**

```bash
dotnet test FlightAssistant.Application.Tests
```

- [ ] **Step 5: Commit**

```bash
git add FlightAssistant.Application.Tests/
git commit -m "test: add unit tests for previously uncovered handlers (#203)"
```

---

### Task 21: Move Admin CRUD validators into command files (GitHub #205)

**Files:**
- Modify: `FlightAssistant.Application/Commands/Admin/Airlines/CreateAirlineCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Airlines/UpdateAirlineCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Airlines/DeleteAirlineCommand.cs`
- (Same pattern for Airports, Lounges, LoyaltyPrograms)
- Modify: `FlightAssistant.Application/Commands/Flights/UntrackFlightCommand.cs` (add validator)
- Modify: `FlightAssistant.Application/Commands/Flights/ToggleFlightNotificationCommand.cs` (add validator)
- Modify: `FlightAssistant.Application/Commands/Flights/SyncFlightStatusCommand.cs` (add validator)
- Modify: `FlightAssistant.Application/Queries/Flights/GetFlightStatusQuery.cs` (add validator)
- Delete: `FlightAssistant.Application/Validators/Admin/AirlineCommandValidators.cs`
- Delete: `FlightAssistant.Application/Validators/Admin/AirportCommandValidators.cs`
- Delete: `FlightAssistant.Application/Validators/Admin/LoungeCommandValidators.cs`
- Delete: `FlightAssistant.Application/Validators/Admin/LoyaltyProgramCommandValidators.cs`

- [ ] **Step 1: Move airline validators into their command files**

Copy `CreateAirlineCommandValidator` from `AirlineCommandValidators.cs` into `CreateAirlineCommand.cs` (after the handler class). Repeat for Update, then delete the separate validator file.

- [ ] **Step 2: Repeat for Airport, Lounge, LoyaltyProgram validators**

Same pattern — move validator class into its corresponding command file.

- [ ] **Step 3: Add missing validators for commands with no validation**

```csharp
// In UntrackFlightCommand.cs:
public sealed class UntrackFlightCommandValidator : AbstractValidator<UntrackFlightCommand>
{
    public UntrackFlightCommandValidator()
    {
        RuleFor(x => x.FlightId).GreaterThan(0);
    }
}

// Same pattern for ToggleFlightNotificationCommand, SyncFlightStatusCommand, GetFlightStatusQuery
// And all Delete commands: RuleFor(x => x.Id).GreaterThan(0)
```

- [ ] **Step 4: Delete the now-empty Validators/Admin/ files**

```bash
rm FlightAssistant.Application/Validators/Admin/AirlineCommandValidators.cs
rm FlightAssistant.Application/Validators/Admin/AirportCommandValidators.cs
rm FlightAssistant.Application/Validators/Admin/LoungeCommandValidators.cs
rm FlightAssistant.Application/Validators/Admin/LoyaltyProgramCommandValidators.cs
```

- [ ] **Step 5: Run tests — FluentValidation auto-discovers validators from the assembly**

```bash
dotnet build --configuration Release && dotnet test
```
Expected: all pass. If a test was relying on a validator being absent, update it.

- [ ] **Step 6: Commit**

```bash
git add -u
git commit -m "refactor: co-locate Admin CRUD validators with their commands; add missing validators (#205)"
```

---

## Phase 7 — API Quality (Nice to Have)

### Task 22: Fix POST /travelers null body + PATCH notify raw bool + PUT membership silent ignore (GitHub #198)

**Files:**
- Create: `FlightAssistant.Contracts/Responses/Travelers/RegisterTravelerResponse.cs`
- Create: `FlightAssistant.Contracts/Requests/Flights/ToggleNotifyRequest.cs`
- Create: `FlightAssistant.Contracts/Requests/Loyalty/UpdateMembershipRequest.cs`
- Modify: `FlightAssistant.API/Endpoints/TravelerEndpoints.cs`
- Modify: `FlightAssistant.API/Endpoints/FlightEndpoints.cs`
- Modify: `FlightAssistant.API/Endpoints/LoyaltyEndpoints.cs`

- [ ] **Step 1: Add RegisterTravelerResponse**

```csharp
// FlightAssistant.Contracts/Responses/Travelers/RegisterTravelerResponse.cs
namespace FlightAssistant.Contracts.Responses.Travelers;
public record RegisterTravelerResponse(Guid TravelerId);
```

Update `TravelerEndpoints.cs`:
```csharp
var travelerId = await mediator.Send(new RegisterTravelerCommand(...), ct);
return Results.Created("/travelers/me", new RegisterTravelerResponse(travelerId));
```
Add `.Produces<RegisterTravelerResponse>(201)` annotation.

- [ ] **Step 2: Add ToggleNotifyRequest**

```csharp
// FlightAssistant.Contracts/Requests/Flights/ToggleNotifyRequest.cs
public record ToggleNotifyRequest(bool Notify);
```

Update `FlightEndpoints.cs` PATCH handler: `[FromBody] ToggleNotifyRequest request` → use `request.Notify`.

- [ ] **Step 3: Add UpdateMembershipRequest**

```csharp
// FlightAssistant.Contracts/Requests/Loyalty/UpdateMembershipRequest.cs
public record UpdateMembershipRequest(
    string AccountNumber,
    int? TierId,
    DateOnly? TierExpiryDate,
    DateOnly? PointsExpiryDate
);
```

Update `LoyaltyEndpoints.cs` PUT handler to use `UpdateMembershipRequest` instead of `AddMembershipRequest`.

- [ ] **Step 4: Run tests**

```bash
dotnet test
```

- [ ] **Step 5: Commit**

```bash
git add -u
git commit -m "fix(api): typed response DTOs for POST /travelers; fix PATCH notify body; fix PUT membership request (#198)"
```

---

## Summary of GitHub Issues

| Issue | Phase | Title |
|-------|-------|-------|
| #189 | 1 | Security: flight ownership check |
| #190 | 1 | Security: AviationStack API key in URL |
| #191 | 2 | Bug: exception handling gaps |
| #192 | 2 | Bug: background sync filter |
| #193 | 2 | Bug: Alliance verify endpoint broken |
| #194 | 3 | Domain: Flight/BaseIntEntity |
| #195 | 3 | Domain: missing private constructors |
| #196 | 3 | Domain: collection encapsulation |
| #197 | 2 | Bug: CompositeProvider.ProviderName |
| #198 | 7 | API: typed DTOs |
| #199 | 4 | CSV: file size/type validation |
| #200 | 4 | CSV: 207 partial errors |
| #201 | 6 | Migration: package versions |
| #202 | 5 | Performance: N+1 queries |
| #203 | 7 | Test: missing handler coverage |
| #204 | 7 | Test: LoungeEligibility unknown-alliance |
| #205 | 7 | Refactor: validator co-location |
