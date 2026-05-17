# Import/Export Test Coverage and Validators Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure the verified-refdata Import/Export/Verify pipeline is fully covered with FluentValidation validators and handler tests. The recent verified-refdata work added 8 import commands, 8 export queries, and a generic verify command — but 4 import handlers are untested and all 8 import commands lack FluentValidation validators.

**Architecture:** Validators are added in the same `.cs` file as their command (following the existing pattern in `Application/Commands/`). Missing handler tests follow the exact pattern in `ImportAlliancesCommandHandlerTests.cs`. Validator tests are added to the existing `ValidatorEdgeCaseTests.cs` using the existing `AssertValid`/`AssertInvalid` helpers.

**Tech Stack:** FluentValidation 12.x, MediatR 13.x, NSubstitute 5.x, xUnit.

---

## What's already covered

| Area | Status |
|------|--------|
| `ImportAlliancesCommandHandler` | ✅ tested |
| `ImportAirportsCommandHandler` | ✅ tested |
| `ImportLoungesCommandHandler` | ✅ tested |
| `ImportLoyaltyProgramsCommandHandler` | ✅ tested |
| `VerifyEntityCommandHandler` | ✅ tested |
| Export query handlers (all 8) | ✅ tested |
| **`ImportCountriesCommandHandler`** | ❌ missing |
| **`ImportAirlinesCommandHandler`** | ❌ missing |
| **`ImportLoungeProvidersCommandHandler`** | ❌ missing |
| **`ImportLoyaltyProgramKindsCommandHandler`** | ❌ missing |
| Validators for all 8 import commands | ❌ missing |

---

## File Map

| Action | File |
|--------|------|
| Modify | `FlightAssistant.Application/Commands/Admin/Import/ImportAlliancesCommand.cs` |
| Modify | `FlightAssistant.Application/Commands/Admin/Import/ImportCountriesCommand.cs` |
| Modify | `FlightAssistant.Application/Commands/Admin/Import/ImportAirportsCommand.cs` |
| Modify | `FlightAssistant.Application/Commands/Admin/Import/ImportAirlinesCommand.cs` |
| Modify | `FlightAssistant.Application/Commands/Admin/Import/ImportLoungeProvidersCommand.cs` |
| Modify | `FlightAssistant.Application/Commands/Admin/Import/ImportLoyaltyProgramKindsCommand.cs` |
| Modify | `FlightAssistant.Application/Commands/Admin/Import/ImportLoungesCommand.cs` |
| Modify | `FlightAssistant.Application/Commands/Admin/Import/ImportLoyaltyProgramsCommand.cs` |
| Create | `FlightAssistant.Application.Tests/Commands/Admin/Import/ImportCountriesCommandHandlerTests.cs` |
| Create | `FlightAssistant.Application.Tests/Commands/Admin/Import/ImportAirlinesCommandHandlerTests.cs` |
| Create | `FlightAssistant.Application.Tests/Commands/Admin/Import/ImportLoungeProvidersCommandHandlerTests.cs` |
| Create | `FlightAssistant.Application.Tests/Commands/Admin/Import/ImportLoyaltyProgramKindsCommandHandlerTests.cs` |
| Modify | `FlightAssistant.Application.Tests/Validators/ValidatorEdgeCaseTests.cs` |

---

## Task 1: Add validators for simple import commands (Alliances, Countries, LoungeProviders, LoyaltyProgramKinds)

These 4 entities have completely flat DTOs with simple string fields — all validators follow the same shape.

**Files:**
- Modify: `FlightAssistant.Application/Commands/Admin/Import/ImportAlliancesCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Import/ImportCountriesCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Import/ImportLoungeProvidersCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Import/ImportLoyaltyProgramKindsCommand.cs`

- [ ] **Step 1: Add validator to ImportAlliancesCommand.cs**

Append after the `ImportAlliancesCommand` record (before the handler class):

```csharp
public sealed class ImportAlliancesCommandValidator : AbstractValidator<ImportAlliancesCommand>
{
    public ImportAlliancesCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Code)
                .NotEmpty()
                .MaximumLength(8)
                .Matches("^[A-Z0-9]+$").WithMessage("'{PropertyName}' must be uppercase alphanumeric.");
            item.RuleFor(d => d.Name)
                .NotEmpty()
                .MaximumLength(200);
        });
    }
}
```

Add the using at the top of the file:
```csharp
using FluentValidation;
```

- [ ] **Step 2: Add validator to ImportCountriesCommand.cs**

```csharp
public sealed class ImportCountriesCommandValidator : AbstractValidator<ImportCountriesCommand>
{
    public ImportCountriesCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Code)
                .NotEmpty()
                .Length(2)
                .Matches("^[A-Z]{2}$").WithMessage("'{PropertyName}' must be a 2-letter uppercase ISO code.");
            item.RuleFor(d => d.Name)
                .NotEmpty()
                .MaximumLength(200);
            item.RuleFor(d => d.Alpha3)
                .Matches("^[A-Z]{3}$").When(d => d.Alpha3 is not null)
                .WithMessage("'{PropertyName}' must be a 3-letter uppercase ISO code.");
        });
    }
}
```

- [ ] **Step 3: Add validator to ImportLoungeProvidersCommand.cs**

```csharp
public sealed class ImportLoungeProvidersCommandValidator : AbstractValidator<ImportLoungeProvidersCommand>
{
    private static readonly string[] ValidKinds =
        Enum.GetNames<LoungeProviderKind>(); // requires: using FlightAssistant.Domain.Lounges.Enums;

    public ImportLoungeProvidersCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Code).NotEmpty().MaximumLength(32);
            item.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.Kind)
                .Must(k => ValidKinds.Contains(k, StringComparer.OrdinalIgnoreCase))
                .WithMessage("'{PropertyName}' must be a valid LoungeProviderKind value.");
        });
    }
}
```

Add using: `using FlightAssistant.Domain.Lounges.Enums;`

- [ ] **Step 4: Add validator to ImportLoyaltyProgramKindsCommand.cs**

```csharp
public sealed class ImportLoyaltyProgramKindsCommandValidator : AbstractValidator<ImportLoyaltyProgramKindsCommand>
{
    public ImportLoyaltyProgramKindsCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Code).NotEmpty().MaximumLength(32);
            item.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.Description).MaximumLength(500).When(d => d.Description is not null);
        });
    }
}
```

- [ ] **Step 5: Build to confirm no errors**

```bash
dotnet build FlightAssistant.Application/ --configuration Release 2>&1 | tail -5
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Application/Commands/Admin/Import/ImportAlliancesCommand.cs \
        FlightAssistant.Application/Commands/Admin/Import/ImportCountriesCommand.cs \
        FlightAssistant.Application/Commands/Admin/Import/ImportLoungeProvidersCommand.cs \
        FlightAssistant.Application/Commands/Admin/Import/ImportLoyaltyProgramKindsCommand.cs
git commit -m "feat(validated-refdata): add FluentValidation validators for simple import commands"
```

---

## Task 2: Add validators for complex import commands (Airports, Airlines, Lounges, LoyaltyPrograms)

**Files:**
- Modify: `FlightAssistant.Application/Commands/Admin/Import/ImportAirportsCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Import/ImportAirlinesCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Import/ImportLoungesCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Import/ImportLoyaltyProgramsCommand.cs`

- [ ] **Step 1: Add validator to ImportAirportsCommand.cs**

```csharp
public sealed class ImportAirportsCommandValidator : AbstractValidator<ImportAirportsCommand>
{
    public ImportAirportsCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Iata)
                .NotEmpty()
                .Length(3)
                .Matches("^[A-Z]{3}$").WithMessage("'{PropertyName}' must be a 3-letter uppercase IATA code.");
            item.RuleFor(d => d.Icao)
                .Length(4)
                .Matches("^[A-Z]{4}$").When(d => d.Icao is not null)
                .WithMessage("'{PropertyName}' must be a 4-letter uppercase ICAO code.");
            item.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.City).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.CountryCode)
                .NotEmpty()
                .Length(2)
                .Matches("^[A-Z]{2}$").WithMessage("'{PropertyName}' must be a 2-letter uppercase ISO country code.");
            item.RuleFor(d => d.Latitude)
                .InclusiveBetween(-90, 90).When(d => d.Latitude.HasValue);
            item.RuleFor(d => d.Longitude)
                .InclusiveBetween(-180, 180).When(d => d.Longitude.HasValue);
        });
    }
}
```

- [ ] **Step 2: Add validator to ImportAirlinesCommand.cs**

```csharp
public sealed class ImportAirlinesCommandValidator : AbstractValidator<ImportAirlinesCommand>
{
    public ImportAirlinesCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d)
                .Must(d => !string.IsNullOrWhiteSpace(d.Iata) || !string.IsNullOrWhiteSpace(d.Icao))
                .WithMessage("Each airline must have at least one of Iata or Icao.");
            item.RuleFor(d => d.Iata)
                .Matches("^[A-Z0-9]{2}$").When(d => d.Iata is not null)
                .WithMessage("'{PropertyName}' must be a 2-character uppercase IATA code.");
            item.RuleFor(d => d.Icao)
                .Matches("^[A-Z]{3}$").When(d => d.Icao is not null)
                .WithMessage("'{PropertyName}' must be a 3-letter uppercase ICAO code.");
            item.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.CountryCode)
                .NotEmpty()
                .Matches("^[A-Z]{2}$").WithMessage("'{PropertyName}' must be a 2-letter uppercase ISO country code.");
            item.RuleFor(d => d.AllianceCode)
                .MaximumLength(8).When(d => d.AllianceCode is not null);
        });
    }
}
```

- [ ] **Step 3: Add validator to ImportLoungesCommand.cs**

```csharp
public sealed class ImportLoungesCommandValidator : AbstractValidator<ImportLoungesCommand>
{
    public ImportLoungesCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.AirportIata)
                .NotEmpty()
                .Length(3)
                .Matches("^[A-Z]{3}$").WithMessage("'{PropertyName}' must be a 3-letter uppercase IATA code.");
            item.RuleFor(d => d.Amenities).MaximumLength(1000).When(d => d.Amenities is not null);
            item.RuleFor(d => d.Providers).NotNull();
            item.RuleForEach(d => d.Providers).ChildRules(p =>
            {
                p.RuleFor(x => x.ProviderCode).NotEmpty().MaximumLength(32);
                p.RuleFor(x => x.Role).NotEmpty();
            });
        });
    }
}
```

- [ ] **Step 4: Add validator to ImportLoyaltyProgramsCommand.cs**

```csharp
public sealed class ImportLoyaltyProgramsCommandValidator : AbstractValidator<ImportLoyaltyProgramsCommand>
{
    public ImportLoyaltyProgramsCommandValidator()
    {
        RuleFor(x => x.Items).NotEmpty();
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(d => d.Code).NotEmpty().MaximumLength(16);
            item.RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
            item.RuleFor(d => d.KindCode).NotEmpty().MaximumLength(32);
            item.RuleFor(d => d.AirlineIata)
                .Matches("^[A-Z0-9]{2}$").When(d => d.AirlineIata is not null)
                .WithMessage("'{PropertyName}' must be a 2-character uppercase IATA code.");
            item.RuleFor(d => d.AllianceCode).MaximumLength(8).When(d => d.AllianceCode is not null);
            item.RuleFor(d => d.Tiers).NotNull();
            item.RuleForEach(d => d.Tiers).ChildRules(t =>
            {
                t.RuleFor(x => x.Code).NotEmpty().MaximumLength(16);
                t.RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
                t.RuleFor(x => x.Rank).GreaterThan(0);
            });
        });
    }
}
```

- [ ] **Step 5: Build**

```bash
dotnet build FlightAssistant.Application/ --configuration Release 2>&1 | tail -5
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Application/Commands/Admin/Import/ImportAirportsCommand.cs \
        FlightAssistant.Application/Commands/Admin/Import/ImportAirlinesCommand.cs \
        FlightAssistant.Application/Commands/Admin/Import/ImportLoungesCommand.cs \
        FlightAssistant.Application/Commands/Admin/Import/ImportLoyaltyProgramsCommand.cs
git commit -m "feat(verified-refdata): add FluentValidation validators for complex import commands"
```

---

## Task 3: Add missing handler tests (Countries, Airlines, LoungeProviders, LoyaltyProgramKinds)

**Files:**
- Create: `FlightAssistant.Application.Tests/Commands/Admin/Import/ImportCountriesCommandHandlerTests.cs`
- Create: `FlightAssistant.Application.Tests/Commands/Admin/Import/ImportAirlinesCommandHandlerTests.cs`
- Create: `FlightAssistant.Application.Tests/Commands/Admin/Import/ImportLoungeProvidersCommandHandlerTests.cs`
- Create: `FlightAssistant.Application.Tests/Commands/Admin/Import/ImportLoyaltyProgramKindsCommandHandlerTests.cs`

- [ ] **Step 1: Write failing tests for Countries**

Create `FlightAssistant.Application.Tests/Commands/Admin/Import/ImportCountriesCommandHandlerTests.cs`:

```csharp
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportCountriesCommandHandlerTests
{
    private readonly ICountryRepository _repo = Substitute.For<ICountryRepository>();

    [Fact]
    public async Task Handle_NewCountry_InsertsAndReturnsInsertedCount()
    {
        _repo.FindByCodeAsync("GB", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new ImportCountriesCommandHandler(_repo);
        var dto = new CountryImportDto("GB", "United Kingdom", "GBR", 826, "Europe", "Northern Europe", false, false, true);

        var result = await handler.Handle(new ImportCountriesCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingCountry_UpdatesAndReturnsUpdatedCount()
    {
        var existing = Country.Create("GB", "Old Name", null, null, null, null, null, null, null);
        _repo.FindByCodeAsync("GB", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new ImportCountriesCommandHandler(_repo);
        var dto = new CountryImportDto("GB", "United Kingdom", "GBR", 826, null, null, null, null, null);

        var result = await handler.Handle(new ImportCountriesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal("United Kingdom", existing.Name);
    }

    [Fact]
    public async Task Handle_ImportedCountry_IsNotVerified()
    {
        _repo.FindByCodeAsync("GB", Arg.Any<CancellationToken>()).ReturnsNull();
        Country? added = null;
        await _repo.AddAsync(Arg.Do<Country>(c => added = c), Arg.Any<CancellationToken>());
        var handler = new ImportCountriesCommandHandler(_repo);
        var dto = new CountryImportDto("GB", "United Kingdom", null, null, null, null, null, null, null);

        await handler.Handle(new ImportCountriesCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.False(added!.IsVerified);
    }
}
```

- [ ] **Step 2: Run to confirm tests fail (types exist, so they should compile and pass immediately if the handler is correct)**

```bash
dotnet test FlightAssistant.Application.Tests/ --filter "ClassName=ImportCountriesCommandHandlerTests" -v normal
```

Expected: All 3 tests pass.

- [ ] **Step 3: Write failing tests for Airlines**

Create `FlightAssistant.Application.Tests/Commands/Admin/Import/ImportAirlinesCommandHandlerTests.cs`:

```csharp
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportAirlinesCommandHandlerTests
{
    private readonly IAirlineRepository _repo = Substitute.For<IAirlineRepository>();

    [Fact]
    public async Task Handle_NewAirline_InsertsAndReturnsInsertedCount()
    {
        _repo.FindByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _repo.FindByIcaoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new ImportAirlinesCommandHandler(_repo);
        var dto = new AirlineImportDto("BA", "BAW", "British Airways", "GB", "OW");

        var result = await handler.Handle(new ImportAirlinesCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddAsync(Arg.Any<Airline>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingAirlineByIata_UpdatesAndReturnsUpdatedCount()
    {
        var existing = Airline.Create("BA", null, "Old Name", "GB", null);
        _repo.FindByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new ImportAirlinesCommandHandler(_repo);
        var dto = new AirlineImportDto("BA", "BAW", "British Airways", "GB", "OW");

        var result = await handler.Handle(new ImportAirlinesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddAsync(Arg.Any<Airline>(), Arg.Any<CancellationToken>());
        Assert.Equal("British Airways", existing.Name);
    }

    [Fact]
    public async Task Handle_IataNotFound_FallsBackToIcaoLookup()
    {
        var existing = Airline.Create(null, "BAW", "British Airways", "GB", null);
        _repo.FindByIataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        _repo.FindByIcaoAsync("BAW", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new ImportAirlinesCommandHandler(_repo);
        var dto = new AirlineImportDto(null, "BAW", "British Airways Updated", "GB", null);

        var result = await handler.Handle(new ImportAirlinesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Equal("British Airways Updated", existing.Name);
    }

    [Fact]
    public async Task Handle_ImportedAirline_IsNotVerified()
    {
        _repo.FindByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _repo.FindByIcaoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        Airline? added = null;
        await _repo.AddAsync(Arg.Do<Airline>(a => added = a), Arg.Any<CancellationToken>());
        var handler = new ImportAirlinesCommandHandler(_repo);
        var dto = new AirlineImportDto("BA", null, "British Airways", "GB", null);

        await handler.Handle(new ImportAirlinesCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.False(added!.IsVerified);
    }
}
```

- [ ] **Step 4: Run Airlines tests**

```bash
dotnet test FlightAssistant.Application.Tests/ --filter "ClassName=ImportAirlinesCommandHandlerTests" -v normal
```

Expected: All 4 tests pass.

- [ ] **Step 5: Write tests for LoungeProviders**

Create `FlightAssistant.Application.Tests/Commands/Admin/Import/ImportLoungeProvidersCommandHandlerTests.cs`:

```csharp
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportLoungeProvidersCommandHandlerTests
{
    private readonly ILoungeRepository _repo = Substitute.For<ILoungeRepository>();

    [Fact]
    public async Task Handle_NewProvider_InsertsAndReturnsInsertedCount()
    {
        _repo.FindProviderByCodeAsync("AMEX", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new ImportLoungeProvidersCommandHandler(_repo);
        var dto = new LoungeProviderImportDto("AMEX", "American Express", "CreditCard", null);

        var result = await handler.Handle(new ImportLoungeProvidersCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddProviderAsync(Arg.Any<LoungeProvider>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingProvider_UpdatesAndReturnsUpdatedCount()
    {
        var existing = LoungeProvider.Create("AMEX", "Old Name", LoungeProviderKind.CreditCard, null);
        _repo.FindProviderByCodeAsync("AMEX", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new ImportLoungeProvidersCommandHandler(_repo);
        var dto = new LoungeProviderImportDto("AMEX", "American Express", "CreditCard", "Premium card lounge access");

        var result = await handler.Handle(new ImportLoungeProvidersCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddProviderAsync(Arg.Any<LoungeProvider>(), Arg.Any<CancellationToken>());
        Assert.Equal("American Express", existing.Name);
    }

    [Fact]
    public async Task Handle_InvalidKind_AddsErrorAndSkips()
    {
        var handler = new ImportLoungeProvidersCommandHandler(_repo);
        var dto = new LoungeProviderImportDto("XYZ", "Unknown", "NotAValidKind", null);

        var result = await handler.Handle(new ImportLoungeProvidersCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Single(result.Errors);
        Assert.Contains("NotAValidKind", result.Errors[0]);
        await _repo.DidNotReceive().AddProviderAsync(Arg.Any<LoungeProvider>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 6: Run LoungeProviders tests**

```bash
dotnet test FlightAssistant.Application.Tests/ --filter "ClassName=ImportLoungeProvidersCommandHandlerTests" -v normal
```

Expected: All 3 tests pass.

- [ ] **Step 7: Write tests for LoyaltyProgramKinds**

Create `FlightAssistant.Application.Tests/Commands/Admin/Import/ImportLoyaltyProgramKindsCommandHandlerTests.cs`:

```csharp
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportLoyaltyProgramKindsCommandHandlerTests
{
    private readonly ILoyaltyRepository _repo = Substitute.For<ILoyaltyRepository>();

    [Fact]
    public async Task Handle_NewKind_InsertsAndReturnsInsertedCount()
    {
        _repo.FindKindByCodeAsync("AIRLINE", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new ImportLoyaltyProgramKindsCommandHandler(_repo);
        var dto = new LoyaltyProgramKindImportDto("AIRLINE", "Airline Miles", "Earn miles on flights");

        var result = await handler.Handle(new ImportLoyaltyProgramKindsCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddKindAsync(Arg.Any<LoyaltyProgramKind>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingKind_UpdatesAndReturnsUpdatedCount()
    {
        var existing = LoyaltyProgramKind.Create("AIRLINE", "Old Name", null);
        _repo.FindKindByCodeAsync("AIRLINE", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new ImportLoyaltyProgramKindsCommandHandler(_repo);
        var dto = new LoyaltyProgramKindImportDto("AIRLINE", "Airline Miles", "Updated description");

        var result = await handler.Handle(new ImportLoyaltyProgramKindsCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddKindAsync(Arg.Any<LoyaltyProgramKind>(), Arg.Any<CancellationToken>());
        Assert.Equal("Airline Miles", existing.Name);
    }
}
```

- [ ] **Step 8: Run LoyaltyProgramKinds tests**

```bash
dotnet test FlightAssistant.Application.Tests/ --filter "ClassName=ImportLoyaltyProgramKindsCommandHandlerTests" -v normal
```

Expected: Both tests pass.

- [ ] **Step 9: Commit**

```bash
git add FlightAssistant.Application.Tests/Commands/Admin/Import/
git commit -m "test(verified-refdata): add missing import handler tests for Countries, Airlines, LoungeProviders, LoyaltyProgramKinds"
```

---

## Task 4: Add validator tests to ValidatorEdgeCaseTests.cs

**Files:**
- Modify: `FlightAssistant.Application.Tests/Validators/ValidatorEdgeCaseTests.cs`

- [ ] **Step 1: Add using directives at the top of ValidatorEdgeCaseTests.cs**

```csharp
using FlightAssistant.Application.Commands.Admin.Import;
```

(Add alongside the existing `using FlightAssistant.Application.Commands.Admin.Airlines;` etc.)

- [ ] **Step 2: Run tests to confirm the new validators are picked up (should fail — sections not added yet)**

```bash
dotnet test FlightAssistant.Application.Tests/ --filter "FullyQualifiedName~ValidatorEdgeCaseTests" -v normal
```

Expected: Existing tests pass. New ones will be added in the next step.

- [ ] **Step 3: Append import validator test sections to ValidatorEdgeCaseTests.cs**

Add these test methods inside the `ValidatorEdgeCaseTests` class, after the existing `// ── Admin: LoyaltyProgram ──` section and before `// ── Flight search ──`:

```csharp
// ── Import: Alliances ─────────────────────────────────────────────────────────

[Fact]
public void ImportAlliances_EmptyCode_Fails()
{
    var v = new ImportAlliancesCommandValidator();
    AssertInvalid(v, new ImportAlliancesCommand([new AllianceImportDto("", "Star Alliance")]), "Code");
}

[Fact]
public void ImportAlliances_LowercaseCode_Fails()
{
    var v = new ImportAlliancesCommandValidator();
    AssertInvalid(v, new ImportAlliancesCommand([new AllianceImportDto("sa", "Star Alliance")]), "Code");
}

[Fact]
public void ImportAlliances_ValidItem_Passes()
{
    var v = new ImportAlliancesCommandValidator();
    AssertValid(v, new ImportAlliancesCommand([new AllianceImportDto("SA", "Star Alliance")]));
}

[Fact]
public void ImportAlliances_EmptyList_Fails()
{
    var v = new ImportAlliancesCommandValidator();
    AssertInvalid(v, new ImportAlliancesCommand([]), "Items");
}

// ── Import: Countries ─────────────────────────────────────────────────────────

[Fact]
public void ImportCountries_InvalidCodeLength_Fails()
{
    var v = new ImportCountriesCommandValidator();
    AssertInvalid(v, new ImportCountriesCommand([new CountryImportDto("GBR", "UK", null, null, null, null, null, null, null)]), "Code");
}

[Fact]
public void ImportCountries_LowercaseCode_Fails()
{
    var v = new ImportCountriesCommandValidator();
    AssertInvalid(v, new ImportCountriesCommand([new CountryImportDto("gb", "UK", null, null, null, null, null, null, null)]), "Code");
}

[Fact]
public void ImportCountries_ValidItem_Passes()
{
    var v = new ImportCountriesCommandValidator();
    AssertValid(v, new ImportCountriesCommand([new CountryImportDto("GB", "United Kingdom", "GBR", 826, null, null, null, null, null)]));
}

// ── Import: Airports ──────────────────────────────────────────────────────────

[Fact]
public void ImportAirports_InvalidIataLength_Fails()
{
    var v = new ImportAirportsCommandValidator();
    AssertInvalid(v, new ImportAirportsCommand([
        new AirportImportDto("LHRX", null, "Heathrow", "London", "GB", null, null, null, null)]), "Iata");
}

[Fact]
public void ImportAirports_LowercaseIata_Fails()
{
    var v = new ImportAirportsCommandValidator();
    AssertInvalid(v, new ImportAirportsCommand([
        new AirportImportDto("lhr", null, "Heathrow", "London", "GB", null, null, null, null)]), "Iata");
}

[Fact]
public void ImportAirports_ValidItem_Passes()
{
    var v = new ImportAirportsCommandValidator();
    AssertValid(v, new ImportAirportsCommand([
        new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", 51.477, -0.461, 83, "Europe/London")]));
}

// ── Import: Airlines ──────────────────────────────────────────────────────────

[Fact]
public void ImportAirlines_NoBothIataAndIcao_Fails()
{
    var v = new ImportAirlinesCommandValidator();
    AssertInvalid(v, new ImportAirlinesCommand([
        new AirlineImportDto(null, null, "British Airways", "GB", null)]));
}

[Fact]
public void ImportAirlines_InvalidCountryCode_Fails()
{
    var v = new ImportAirlinesCommandValidator();
    AssertInvalid(v, new ImportAirlinesCommand([
        new AirlineImportDto("BA", null, "British Airways", "GBR", null)]), "CountryCode");
}

[Fact]
public void ImportAirlines_ValidItemWithIataOnly_Passes()
{
    var v = new ImportAirlinesCommandValidator();
    AssertValid(v, new ImportAirlinesCommand([
        new AirlineImportDto("BA", null, "British Airways", "GB", null)]));
}

// ── Import: LoungeProviders ───────────────────────────────────────────────────

[Fact]
public void ImportLoungeProviders_EmptyCode_Fails()
{
    var v = new ImportLoungeProvidersCommandValidator();
    AssertInvalid(v, new ImportLoungeProvidersCommand([
        new LoungeProviderImportDto("", "AMEX", "CreditCard", null)]), "Code");
}

[Fact]
public void ImportLoungeProviders_InvalidKind_Fails()
{
    var v = new ImportLoungeProvidersCommandValidator();
    AssertInvalid(v, new ImportLoungeProvidersCommand([
        new LoungeProviderImportDto("AMEX", "American Express", "NotAKind", null)]), "Kind");
}

// ── Import: LoyaltyPrograms ───────────────────────────────────────────────────

[Fact]
public void ImportLoyaltyPrograms_CodeExceeds16_Fails()
{
    var v = new ImportLoyaltyProgramsCommandValidator();
    AssertInvalid(v, new ImportLoyaltyProgramsCommand([
        new LoyaltyProgramImportDto(new string('A', 17), "Club", "AIRLINE", null, null, [])]), "Code");
}

[Fact]
public void ImportLoyaltyPrograms_TierWithZeroRank_Fails()
{
    var v = new ImportLoyaltyProgramsCommandValidator();
    var tier = new LoyaltyTierImportDto("GOLD", "Gold", 0, null, null);
    AssertInvalid(v, new ImportLoyaltyProgramsCommand([
        new LoyaltyProgramImportDto("BAEC", "Executive Club", "AIRLINE", null, null, [tier])]), "Rank");
}

[Fact]
public void ImportLoyaltyPrograms_ValidItemWithTiers_Passes()
{
    var v = new ImportLoyaltyProgramsCommandValidator();
    var tier = new LoyaltyTierImportDto("GOLD", "Gold", 1, null, null);
    AssertValid(v, new ImportLoyaltyProgramsCommand([
        new LoyaltyProgramImportDto("BAEC", "Executive Club", "AIRLINE", "BA", null, [tier])]));
}
```

- [ ] **Step 4: Run all validator tests**

```bash
dotnet test FlightAssistant.Application.Tests/ --filter "FullyQualifiedName~ValidatorEdgeCaseTests" -v normal
```

Expected: All tests pass (existing + new ones).

- [ ] **Step 5: Run the full test suite**

```bash
dotnet test --configuration Release -v normal 2>&1 | tail -15
```

Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Application.Tests/Validators/ValidatorEdgeCaseTests.cs
git commit -m "test(verified-refdata): add validator edge-case tests for all 8 import commands"
```
