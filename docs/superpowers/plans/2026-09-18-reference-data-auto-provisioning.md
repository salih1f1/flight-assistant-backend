# Reference-Data Auto-Provisioning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When `POST /flights` finds no row for the airline, an airport or a country, look the entity up in the external sources we already use for verification, create and verify the row, and reserve 404 for an entity the source genuinely does not have.

**Architecture:** A new scoped Application service, `ReferenceDataProvisioner`, sits behind `TrackFlightCommandHandler`'s existing id lookups. It fetches through `IVerificationSourceRegistry` (no new HTTP code), writes reference rows in its own DI scope carrying the `system` actor so the traveler's own writes stay unaudited, and is fronted by a negative cache and the AeroAPI budget guard. The whole step runs under one wall-clock deadline; exceeding it is a 503, not a 404.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core + PostgreSQL, MediatR, FluentValidation, xUnit + NSubstitute, Testcontainers (PostgreSQL-backed suite).

**Spec:** `docs/superpowers/specs/2026-09-18-reference-data-auto-provisioning-design.md`

**Issue:** #465

## Global Constraints

- Branch off `development`; never commit to `development` or `main`. This plan runs in the worktree on `feature/465-reference-data-auto-provisioning`.
- Tests are xUnit with NSubstitute and plain `Assert.*` — this repo does not use FluentAssertions.
- `--filter "FullyQualifiedName~..."` is the only matcher that works. `ClassName=` and `Name~` match nothing, run zero tests and report no failure, so a filtered run with a typo looks exactly like a passing run.
- All timestamps UTC, properties suffixed `Utc`, columns `timestamptz`.
- Commands, validators and handlers live in one file: `Application/Commands/{Domain}/{CommandName}.cs`.
- External service config uses `AddOptions<T>().BindConfiguration(section).ValidateOnStart()`.
- A format or classification rule shared by two call sites lives in **one** constant, never two copies.
- `DomainException(message, statusCode, diagnosticDetail)`: `DiagnosticDetail` renders outside Production only and must never carry credentials or request URLs.
- A 503 `DomainException` already gets `Retry-After: 60` from `GlobalExceptionHandler` — do not add header plumbing.
- `spec/openapi.json` is generated. Never hand-edit it; regenerate with `scripts/generate-openapi.sh` from the repo root.
- Never declare both `.ProducesValidationProblem()` and `.ProducesProblem(400)` on one route.
- 401, 403 and 429 are deliberately not declared per-route. Do not add them.
- The API test suite runs on the InMemory provider, which enforces **no** foreign key. Anything depending on FK behaviour needs a PostgreSQL-backed test in `FlightAssistant.Infrastructure.Tests` (Docker required).

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `FlightAssistant.Application/Verification/VerificationExtraValues.cs` | The one constant naming the non-checked-column key AeroAPI uses to carry an operator's country name. |
| `FlightAssistant.Application/Interfaces/Verification/ICountryNameResolver.cs` | Resolve a country *name* to a two-letter code. Implemented by the mledoze source. |
| `FlightAssistant.Application/ReferenceData/ReferenceProvisionContracts.cs` | `MissingReference`, `ReferenceProvisionStatus`, `ReferenceProvisionResult`, `IReferenceDataProvisioner`. |
| `FlightAssistant.Application/ReferenceData/IReferenceProvisioningOptions.cs` | Application-side view of the `ReferenceProvisioning` config section. |
| `FlightAssistant.Application/ReferenceData/ReferenceDataProvisioner.cs` | The service itself: fetch, order, construct, verify, save. |
| `FlightAssistant.API/Infrastructure/ReferenceProvisioningOptions.cs` | Bound + validated options implementing the Application interface. |
| `FlightAssistant.API/Infrastructure/ReferenceProvisioningServiceCollectionExtensions.cs` | `AddReferenceProvisioning()` registration. |
| `FlightAssistant.Application.Tests/ReferenceData/ReferenceDataProvisionerTests.cs` | Provisioner behaviour against fakes. |
| `FlightAssistant.Infrastructure.Tests/Persistance/ReferenceProvisioningPostgresTests.cs` | FK ordering and audit rows, against real PostgreSQL. |

**Modified:**

| File | Change |
|---|---|
| `FlightAssistant.Infrastructure/ExternalServices/Verification/MledozeCountriesVerificationSource.cs` | Build a normalised name index during parse; implement `ICountryNameResolver`. |
| `FlightAssistant.Infrastructure/ExternalServices/Verification/AeroApiVerificationSource.cs` | `MapAirline` also emits the raw `country` name. |
| `FlightAssistant.Infrastructure/DependencyInjection.cs` | Forward `ICountryNameResolver` to the mledoze singleton. |
| `FlightAssistant.Application/DependencyInjection.cs` | Register `IReferenceDataProvisioner`. |
| `FlightAssistant.Application/Commands/Flights/TrackFlightCommand.cs` | Cap-before-provision, call the provisioner, map outcomes. |
| `FlightAssistant.API/Program.cs` | `AddReferenceProvisioning()`. |
| `FlightAssistant.API/Endpoints/FlightEndpoints.cs` | Describe the new 503 on the track route. |
| `FlightAssistant.API/appsettings.json` | `ReferenceProvisioning` defaults. |
| `CLAUDE.md` | Document the behaviour and the new config keys. |
| `spec/openapi.json` | Regenerated. |

---

## Task 1: Resolve a country name to a code (mledoze)

**Files:**
- Create: `FlightAssistant.Application/Interfaces/Verification/ICountryNameResolver.cs`
- Modify: `FlightAssistant.Infrastructure/ExternalServices/Verification/MledozeCountriesVerificationSource.cs`
- Modify: `FlightAssistant.Infrastructure/DependencyInjection.cs:132-136`
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/Verification/MledozeCountriesVerificationSourceTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `ICountryNameResolver.TryResolveCodeByNameAsync(string? countryName, CancellationToken ct) -> Task<CountryCodeResolution>`, and `CountryCodeResolution(string? Code, string? Error)` with `bool IsError => Error is not null`, plus statics `NotFound`, `Found(string code)`, `Failed(string error)`. Task 6 consumes all of these.

**Why:** `Airline.Create` needs a `CountryCode` foreign key, but `aeroapi` reports an operator's country as a name. mledoze already holds `name.common` → `cca2`; it just has no reverse index. Matching reuses `VerificationValueNormalizer.NormalizeText`, the same accent/case/punctuation-insensitive rule the comparer applies to Text columns, so "Türkiye" and "Turkiye" resolve alike.

- [ ] **Step 1: Write the failing tests**

Append to `FlightAssistant.Infrastructure.Tests/ExternalServices/Verification/MledozeCountriesVerificationSourceTests.cs`. Use that file's existing helpers for building the source over a stub transport — `CreateSource(json)` and a throwing-transport variant; if they are named differently there, use the existing names rather than renaming them.

```csharp
    private const string TwoCountryDataset = """
        [
          {"cca2":"GB","cca3":"GBR","ccn3":826,"name":{"common":"United Kingdom"},"region":"Europe","subregion":"Northern Europe"},
          {"cca2":"TR","cca3":"TUR","ccn3":792,"name":{"common":"Türkiye"},"region":"Asia","subregion":"Western Asia"}
        ]
        """;

    [Fact]
    public async Task TryResolveCodeByNameAsync_ExactName_ReturnsCode()
    {
        var source = CreateSource(TwoCountryDataset);

        var result = await source.TryResolveCodeByNameAsync("United Kingdom", CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("GB", result.Code);
    }

    [Fact]
    public async Task TryResolveCodeByNameAsync_NormalisesAccentsAndCase()
    {
        // The comparer's own Text rule, reused: a source spelling it without the diacritic must
        // still resolve, or no Turkish airline would ever provision.
        var source = CreateSource(TwoCountryDataset);

        var result = await source.TryResolveCodeByNameAsync("turkiye", CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("TR", result.Code);
    }

    [Fact]
    public async Task TryResolveCodeByNameAsync_UnknownName_ReturnsNullCodeAndNoError()
    {
        var source = CreateSource(TwoCountryDataset);

        var result = await source.TryResolveCodeByNameAsync("Atlantis", CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Null(result.Code);
    }

    [Fact]
    public async Task TryResolveCodeByNameAsync_BlankName_ReturnsNullWithoutLoadingTheDataset()
    {
        // A transport that throws on any request: reaching the download at all fails this test.
        var source = CreateSourceWithThrowingTransport();

        var result = await source.TryResolveCodeByNameAsync("  ", CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Null(result.Code);
    }

    [Fact]
    public async Task TryResolveCodeByNameAsync_LoadFails_ReportsErrorNotAMiss()
    {
        // A failed load must never look like "no such country" — that would 404 a real airline.
        var source = CreateSourceWithThrowingTransport();

        var result = await source.TryResolveCodeByNameAsync("United Kingdom", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Null(result.Code);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~MledozeCountriesVerificationSourceTests.TryResolveCodeByNameAsync"
```

Expected: FAIL to compile — `MledozeCountriesVerificationSource` has no `TryResolveCodeByNameAsync`.

- [ ] **Step 3: Add the Application interface**

Create `FlightAssistant.Application/Interfaces/Verification/ICountryNameResolver.cs`:

```csharp
namespace FlightAssistant.Application.Interfaces.Verification
{
    /// <summary>
    /// One country-name lookup's answer. Shaped like <see cref="VerificationSourceFetchResult"/>:
    /// a miss and a failure are different outcomes, because a failed dataset load must never be
    /// reported as "no such country" -- that would turn a transient outage into a 404 for an
    /// airline that genuinely exists (#465).
    /// </summary>
    public sealed record CountryCodeResolution(string? Code, string? Error)
    {
        public bool IsError => Error is not null;

        public static readonly CountryCodeResolution NotFound = new(null, null);

        public static CountryCodeResolution Found(string code)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(code);
            return new CountryCodeResolution(code, null);
        }

        public static CountryCodeResolution Failed(string error)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(error);
            return new CountryCodeResolution(null, error);
        }
    }

    /// <summary>
    /// Resolves a country's <i>name</i> to its two-letter code (#465). Needed because
    /// <c>aeroapi</c> reports an operator's country as a name ("United Kingdom") and never a code,
    /// while <c>Airline.CountryCode</c> is a required foreign key into <c>countries</c>.
    /// Implemented by <c>MledozeCountriesVerificationSource</c>, which already holds
    /// <c>name.common</c> -> <c>cca2</c> for every country: this is a reverse index over the
    /// dataset that source downloads anyway, not a second download.
    /// </summary>
    public interface ICountryNameResolver
    {
        /// <summary>
        /// The two-letter code for <paramref name="countryName"/>, matched with the same
        /// accent/case/punctuation-insensitive rule <c>VerificationComparer</c> applies to Text
        /// columns. A blank name returns <see cref="CountryCodeResolution.NotFound"/> without
        /// loading the dataset.
        /// </summary>
        Task<CountryCodeResolution> TryResolveCodeByNameAsync(string? countryName, CancellationToken ct = default);
    }
}
```

- [ ] **Step 4: Implement it on the mledoze source**

In `MledozeCountriesVerificationSource.cs`:

1. Add the interface to the class declaration:

```csharp
    public sealed class MledozeCountriesVerificationSource : IVerificationSource, ICountryNameResolver
```

2. Replace the cached dataset type with a record holding both indexes, and update the field and its construction:

```csharp
        /// <summary>The parsed dataset: the by-code index every lookup uses, plus a by-name index
        /// for <see cref="ICountryNameResolver"/> (#465). Both are built in one parse pass, so the
        /// name index costs one dictionary and no extra download.</summary>
        private sealed record Dataset(
            IReadOnlyDictionary<string, Row> ByCode,
            IReadOnlyDictionary<string, string> CodeByNormalizedName);

        private readonly VerificationDatasetCache<Dataset> _cache;
```

```csharp
            _cache = new VerificationDatasetCache<Dataset>(timeProvider, MaxCacheAge);
```

3. In `LookupAsync`, read `ByCode` off the cached `Dataset`:

```csharp
            Dataset dataset;
            try
            {
                dataset = await _cache.GetAsync(DownloadAsync, ct).ConfigureAwait(false);
            }
            catch (VerificationSourceFetchException ex)
            {
                // Logged once, with the whole exception, where the load failed (DownloadAsync); a
                // lookup inside the failure cooldown just hands on the same fixed-category error.
                return VerificationSourceFetchResult.SourceError(ex.Message);
            }

            if (string.IsNullOrWhiteSpace(key.CountryCode) || !dataset.ByCode.TryGetValue(key.CountryCode, out var row))
                return VerificationSourceFetchResult.NotFound;

            return VerificationSourceFetchResult.Found(ToValues(row));
```

4. Change `DownloadAsync` and `ParseAsync` to return `Dataset`. In `ParseAsync`, declare the second index next to `byCode`:

```csharp
            var byCode = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            var codeByName = new Dictionary<string, string>(StringComparer.Ordinal);
```

Immediately after the existing `byCode[record.Cca2] = new Row(...);` assignment:

```csharp
                // The reverse index for ICountryNameResolver (#465). Normalised with the comparer's
                // own Text rule so an accent or casing difference in an upstream country name still
                // resolves. First one wins, unlike byCode's last-one-wins: the real dataset has no
                // duplicate common name, and on a reshaped file keeping the earlier record is
                // arbitrary but at least stable across runs.
                var normalizedName = VerificationValueNormalizer.NormalizeText(record.Name.Common);
                if (!string.IsNullOrEmpty(normalizedName))
                    codeByName.TryAdd(normalizedName, record.Cca2);
```

Keep the existing zero-surviving-records check exactly as it is (it guards `byCode.Count == 0`), then return both indexes:

```csharp
            return new Dataset(byCode, codeByName);
```

5. Add the interface implementation:

```csharp
        /// <inheritdoc />
        public async Task<CountryCodeResolution> TryResolveCodeByNameAsync(
            string? countryName, CancellationToken ct = default)
        {
            // A blank name is a miss, not a reason to download a dataset.
            if (string.IsNullOrWhiteSpace(countryName))
                return CountryCodeResolution.NotFound;

            var normalized = VerificationValueNormalizer.NormalizeText(countryName);
            if (string.IsNullOrEmpty(normalized))
                return CountryCodeResolution.NotFound;

            Dataset dataset;
            try
            {
                dataset = await _cache.GetAsync(DownloadAsync, ct).ConfigureAwait(false);
            }
            catch (VerificationSourceFetchException ex)
            {
                // A failed load is never a miss: reporting "no such country" here would 404 an
                // airline that genuinely exists.
                return CountryCodeResolution.Failed(ex.Message);
            }

            return dataset.CodeByNormalizedName.TryGetValue(normalized, out var code)
                ? CountryCodeResolution.Found(code)
                : CountryCodeResolution.NotFound;
        }
```

Add `using FlightAssistant.Application.Interfaces.Verification;` if it is not already imported.

- [ ] **Step 5: Register it in DI**

In `FlightAssistant.Infrastructure/DependencyInjection.cs`, immediately after the existing mledoze `IVerificationSource` forward (around line 136):

```csharp
            // The same singleton, so the name index rides on the dataset the source already
            // downloads and caches (#465) rather than a second copy of it.
            services.AddSingleton<ICountryNameResolver>(sp => sp.GetRequiredService<MledozeCountriesVerificationSource>());
```

- [ ] **Step 6: Run the tests to verify they pass**

```
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~MledozeCountriesVerificationSourceTests"
```

Expected: PASS, including every pre-existing test in that class — the `Dataset` refactor must not change lookup behaviour.

- [ ] **Step 7: Commit**

Stage `ICountryNameResolver.cs`, `MledozeCountriesVerificationSource.cs`, `DependencyInjection.cs` and the test file, then commit with message:

```
feat(verification): resolve a country name to its code via mledoze (#465)
```

---

## Task 2: AeroAPI surfaces the operator's country name

**Files:**
- Create: `FlightAssistant.Application/Verification/VerificationExtraValues.cs`
- Modify: `FlightAssistant.Infrastructure/ExternalServices/Verification/AeroApiVerificationSource.cs:248-267`
- Test: `FlightAssistant.Infrastructure.Tests/ExternalServices/Verification/AeroApiVerificationSourceTests.cs`
- Test: `FlightAssistant.Application.Tests/Verification/VerificationComparerTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `VerificationExtraValues.AirlineCountryName` — the string `"CountryName"`, the key Task 6 reads off an airline fetch result.

**Why:** `MapAirline` emits only the three checked columns. The provisioner needs the raw country name too. `VerificationComparer` reads only columns registered in `VerificationCheckedColumns`, so an extra key cannot change a verification outcome — the second test pins that, because it is the whole safety argument for this approach.

- [ ] **Step 1: Write the failing tests**

Append to `FlightAssistant.Infrastructure.Tests/ExternalServices/Verification/AeroApiVerificationSourceTests.cs`, using that file's existing helpers for a stubbed AeroAPI response and for reading a fixture (match the existing names; the fixture `operator-baw.json` already exists):

```csharp
    [Fact]
    public async Task AirlineLookup_AlsoCarriesTheRawCountryName()
    {
        // Airline.CountryCode is a required FK but deliberately not a checked column (#401 R1),
        // so this name is the only country information AeroAPI gives us. #465 resolves it to a code.
        var source = CreateSourceReturning(HttpStatusCode.OK, ReadFixture("operator-baw.json"));

        var result = await source.LookupAsync(
            VerificationEntityType.Airline,
            VerificationSourceLookupKey.ForAirline("BA", "BAW"),
            CancellationToken.None);

        Assert.False(result.IsError);
        var values = result.Lookup!.Values!;
        Assert.Equal("United Kingdom", values[VerificationExtraValues.AirlineCountryName]);
    }

    [Fact]
    public async Task AirlineLookup_StillCarriesExactlyTheCheckedColumns()
    {
        var source = CreateSourceReturning(HttpStatusCode.OK, ReadFixture("operator-baw.json"));

        var result = await source.LookupAsync(
            VerificationEntityType.Airline,
            VerificationSourceLookupKey.ForAirline("BA", "BAW"),
            CancellationToken.None);

        var values = result.Lookup!.Values!;
        foreach (var column in VerificationCheckedColumns.GetColumns(VerificationEntityType.Airline))
            Assert.True(values.ContainsKey(column.Name), $"missing checked column {column.Name}");
        Assert.Equal("BA", values[nameof(Airline.Iata)]);
        Assert.Equal("BAW", values[nameof(Airline.Icao)]);
        Assert.Equal("British Airways", values[nameof(Airline.Name)]);
    }
```

Append to `FlightAssistant.Application.Tests/Verification/VerificationComparerTests.cs`, matching the `VerificationComparer.Compare` call shape the neighbouring tests in that file already use (do not change the comparer to fit this test):

```csharp
    [Fact]
    public void Compare_IgnoresValuesThatAreNotCheckedColumns()
    {
        // #465 has AeroAPI carry the operator's country name alongside the checked columns. The
        // comparer must not see it — otherwise every airline check would report a difference for a
        // column our own projected row does not even contain.
        var airline = Airline.Create("BA", "BAW", "British Airways", "GB");
        var sourceValues = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(Airline.Iata)] = "BA",
            [nameof(Airline.Icao)] = "BAW",
            [nameof(Airline.Name)] = "British Airways",
            [VerificationExtraValues.AirlineCountryName] = "United Kingdom"
        };

        var result = VerificationComparer.Compare(
            VerificationEntityType.Airline,
            VerificationCheckedColumns.Project(airline),
            new Dictionary<string, IReadOnlyDictionary<string, string?>> { ["aeroapi"] = sourceValues });

        Assert.Empty(result.Differences);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiVerificationSourceTests.AirlineLookup_AlsoCarriesTheRawCountryName"
```

Expected: FAIL to compile — `VerificationExtraValues` does not exist.

- [ ] **Step 3: Add the shared constant**

Create `FlightAssistant.Application/Verification/VerificationExtraValues.cs`:

```csharp
namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// Keys a source may put in <c>VerificationSourceFetchResult.Found</c> that are <b>not</b>
    /// checked columns (#465).
    ///
    /// <para>
    /// <see cref="VerificationComparer"/> reads only the columns
    /// <see cref="VerificationCheckedColumns.GetColumns"/> registers and silently ignores anything
    /// else -- its own documented behaviour -- so a key here can never change a verification
    /// outcome. It exists because reference-data provisioning needs a field the check deliberately
    /// does not compare.
    /// </para>
    ///
    /// <para>
    /// <b>Do not add a key here to avoid registering a real checked column.</b> This is only for
    /// values that genuinely cannot be compared against one of our own columns. Today there is
    /// exactly one: an airline's country, which <c>aeroapi</c> reports as a name ("United Kingdom")
    /// and never as the two-letter code <c>Airline.CountryCode</c> stores -- which is precisely why
    /// #401 R1 removed that column from the airline checked-column list.
    /// </para>
    /// </summary>
    public static class VerificationExtraValues
    {
        /// <summary>The airline's country as the source names it, e.g. <c>"United Kingdom"</c>.
        /// Resolved to a code by <c>ICountryNameResolver</c>.</summary>
        public const string AirlineCountryName = "CountryName";
    }
}
```

- [ ] **Step 4: Emit it from `MapAirline`**

In `AeroApiVerificationSource.cs`, replace `MapAirline`'s body with:

```csharp
        private static IReadOnlyDictionary<string, string?> MapAirline(JsonElement root)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var column in VerificationCheckedColumns.GetColumns(VerificationEntityType.Airline))
            {
                values[column.Name] = column.Name switch
                {
                    nameof(Airline.Iata) => GetString(root, "iata"),
                    nameof(Airline.Icao) => GetString(root, "icao"),
                    nameof(Airline.Name) => GetString(root, "name"),
                    // Airline.CountryCode is deliberately not a checked column (R1) -- AeroAPI's
                    // `country` here is a name ("United Kingdom"), never a code, and
                    // VerificationCheckedColumns.GetColumns(Airline) no longer lists it, so this
                    // switch is never asked to map it.
                    _ => throw new InvalidOperationException(
                        $"{SourceName} has no mapping for checked Airline column '{column.Name}'.")
                };
            }

            // Not a checked column, so VerificationComparer ignores it (#465). Carried because
            // Airline.CountryCode is a required foreign key and this name is the only country
            // information AeroAPI gives us; ICountryNameResolver turns it into a code.
            values[VerificationExtraValues.AirlineCountryName] = GetString(root, "country");

            return values;
        }
```

Add `using FlightAssistant.Application.Verification;` if it is not already imported.

- [ ] **Step 5: Run the tests to verify they pass**

```
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AeroApiVerificationSourceTests"
```

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~VerificationComparerTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

Stage `VerificationExtraValues.cs`, `AeroApiVerificationSource.cs` and both test files, then commit with message:

```
feat(verification): carry the operator country name on an aeroapi airline lookup (#465)
```

---

## Task 3: The `ReferenceProvisioning` configuration section

**Files:**
- Create: `FlightAssistant.Application/ReferenceData/IReferenceProvisioningOptions.cs`
- Create: `FlightAssistant.API/Infrastructure/ReferenceProvisioningOptions.cs`
- Create: `FlightAssistant.API/Infrastructure/ReferenceProvisioningServiceCollectionExtensions.cs`
- Modify: `FlightAssistant.API/Program.cs` (next to the other `Add*` registrations)
- Modify: `FlightAssistant.API/appsettings.json`
- Test: `FlightAssistant.API.Tests/Configuration/ReferenceProvisioningOptionsTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `IReferenceProvisioningOptions` with `bool Enabled`, `TimeSpan Budget`, `TimeSpan NegativeCacheTtl`. Tasks 4, 7 and 9 consume it.

**Why:** Application must not read `IConfiguration`; it reads a translation interface, the same pattern `IVerificationJobOptions` and `IFlightSyncOptions` already use. Validation is on start, so an out-of-range value stops the host rather than surfacing mid-request.

- [ ] **Step 1: Write the failing test**

Create `FlightAssistant.API.Tests/Configuration/ReferenceProvisioningOptionsTests.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using FlightAssistant.API.Infrastructure;

namespace FlightAssistant.API.Tests.Configuration;

public class ReferenceProvisioningOptionsTests
{
    private static IReadOnlyList<ValidationResult> Validate(ReferenceProvisioningOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(Validate(new ReferenceProvisioningOptions()));
    }

    [Fact]
    public void Defaults_AreEnabledTenSecondsAndADay()
    {
        var options = new ReferenceProvisioningOptions();

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Budget);
        Assert.Equal(TimeSpan.FromHours(24), options.NegativeCacheTtl);
    }

    [Theory]
    [InlineData("00:00:00")]   // below the 1 s floor — a zero budget would fail every provision
    [InlineData("00:01:01")]   // above the 60 s ceiling — a traveler request must not hang this long
    public void Budget_OutsideRange_FailsValidation(string budget)
    {
        var options = new ReferenceProvisioningOptions { Budget = TimeSpan.Parse(budget) };

        Assert.NotEmpty(Validate(options));
    }

    [Fact]
    public void NegativeCacheTtl_MayBeZero_WhichDisablesTheCache()
    {
        var options = new ReferenceProvisioningOptions { NegativeCacheTtl = TimeSpan.Zero };

        Assert.Empty(Validate(options));
    }

    [Fact]
    public void NegativeCacheTtl_BeyondSevenDays_FailsValidation()
    {
        var options = new ReferenceProvisioningOptions { NegativeCacheTtl = TimeSpan.FromDays(8) };

        Assert.NotEmpty(Validate(options));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~ReferenceProvisioningOptionsTests"
```

Expected: FAIL to compile — `ReferenceProvisioningOptions` does not exist.

- [ ] **Step 3: Add the Application-side interface**

Create `FlightAssistant.Application/ReferenceData/IReferenceProvisioningOptions.cs`:

```csharp
namespace FlightAssistant.Application.ReferenceData
{
    /// <summary>
    /// The <c>ReferenceProvisioning</c> configuration section as Application sees it (#465) --
    /// the same translation-interface pattern as <c>IVerificationJobOptions</c> and
    /// <c>IFlightSyncOptions</c>, so Application never reads <c>IConfiguration</c>. Bound and
    /// validated on start in the API layer.
    /// </summary>
    public interface IReferenceProvisioningOptions
    {
        /// <summary>Whether a missing airline, airport or country is looked up in the external
        /// sources at all. When false, <c>POST /flights</c> answers exactly the 404 it did before
        /// #465.</summary>
        bool Enabled { get; }

        /// <summary>One wall-clock budget for the whole provisioning step, however many entities
        /// and sources it touches. Exceeding it is a 503, never a 404: we did not learn that the
        /// entity is absent, only that we ran out of time. The shared dataset download keeps
        /// running afterwards, so a retry finds it warm.</summary>
        TimeSpan Budget { get; }

        /// <summary>How long "this source has no such entity" is remembered, so a repeated bogus
        /// flight designator costs nothing after the first attempt. <see cref="TimeSpan.Zero"/>
        /// disables the cache, so every attempt re-queries the source.</summary>
        TimeSpan NegativeCacheTtl { get; }
    }
}
```

- [ ] **Step 4: Add the bound options and its registration**

Create `FlightAssistant.API/Infrastructure/ReferenceProvisioningOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using FlightAssistant.Application.ReferenceData;

namespace FlightAssistant.API.Infrastructure
{
    /// <summary>
    /// Configuration for reference-data auto-provisioning (#465), from the
    /// <c>ReferenceProvisioning</c> section. Bound and validated on start by
    /// <see cref="ReferenceProvisioningServiceCollectionExtensions.AddReferenceProvisioning"/>, so
    /// an out-of-range value stops the host from starting instead of surfacing inside a traveler's
    /// tracking request. Application reads it through <see cref="IReferenceProvisioningOptions"/>.
    /// </summary>
    public sealed class ReferenceProvisioningOptions : IReferenceProvisioningOptions
    {
        public const string SectionName = "ReferenceProvisioning";

        /// <inheritdoc />
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// The ceiling is a minute because this budget is spent inside a traveler's own
        /// <c>POST /flights</c> request; the floor is a second because a zero budget would fail
        /// every provision before a source could answer.
        /// </summary>
        [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
        public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary><c>00:00:00</c> disables the negative cache, matching the convention
        /// <c>Verification:IdleRelease</c> and <c>Verification:ErrorCooldown</c> already use for
        /// zero.</summary>
        [Range(typeof(TimeSpan), "00:00:00", "7.00:00:00")]
        public TimeSpan NegativeCacheTtl { get; set; } = TimeSpan.FromHours(24);
    }
}
```

Create `FlightAssistant.API/Infrastructure/ReferenceProvisioningServiceCollectionExtensions.cs`:

```csharp
using FlightAssistant.Application.ReferenceData;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.Infrastructure
{
    public static class ReferenceProvisioningServiceCollectionExtensions
    {
        /// <summary>Binds and validates <see cref="ReferenceProvisioningOptions"/> and exposes it to
        /// Application as <see cref="IReferenceProvisioningOptions"/> (#465).</summary>
        public static IServiceCollection AddReferenceProvisioning(this IServiceCollection services)
        {
            services.AddOptions<ReferenceProvisioningOptions>()
                .BindConfiguration(ReferenceProvisioningOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddSingleton<IReferenceProvisioningOptions>(sp =>
                sp.GetRequiredService<IOptions<ReferenceProvisioningOptions>>().Value);

            return services;
        }
    }
}
```

In `FlightAssistant.API/Program.cs`, next to the existing `builder.Services.AddVerificationJob();` call (around line 158):

```csharp
builder.Services.AddReferenceProvisioning();
```

In `FlightAssistant.API/appsettings.json`, add a sibling of the existing `Verification` section:

```json
  "ReferenceProvisioning": {
    "Enabled": true,
    "Budget": "00:00:10",
    "NegativeCacheTtl": "24:00:00"
  },
```

- [ ] **Step 5: Run the test to verify it passes**

```
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~ReferenceProvisioningOptionsTests"
```

Expected: PASS.

- [ ] **Step 6: Run the whole API suite — the host must still start**

```
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj
```

Expected: PASS. `ValidateOnStart` means a bad default would fail every integration test, so a green suite is the proof the section binds.

- [ ] **Step 7: Commit**

Stage the two new API files, the Application interface, `Program.cs`, `appsettings.json` and the test, then commit with message:

```
feat(config): add the ReferenceProvisioning section (#465)
```

---

## Task 4: The provisioner — contracts, and airports

**Files:**
- Create: `FlightAssistant.Application/ReferenceData/ReferenceProvisionContracts.cs`
- Create: `FlightAssistant.Application/ReferenceData/ReferenceDataProvisioner.cs`
- Modify: `FlightAssistant.Application/DependencyInjection.cs`
- Test: `FlightAssistant.Application.Tests/ReferenceData/ReferenceDataProvisionerTests.cs`

**Interfaces:**
- Consumes: `IReferenceProvisioningOptions` (Task 3).
- Produces: `MissingReference(VerificationEntityType Type, string Code)`; `ReferenceProvisionStatus { Provisioned, NotFound, Unavailable }`; `ReferenceProvisionResult(Status, FailedType, FailedCode, DiagnosticDetail)` with statics `Provisioned`, `NotFound(type, code)` and `Unavailable(type, code, diagnosticDetail)`; `IReferenceDataProvisioner.ProvisionAsync(IReadOnlyCollection<MissingReference>, CancellationToken) -> Task<ReferenceProvisionResult>`. Tasks 5–10 consume all of these.

**Why this shape:** one entry point rather than one method per entity type, so Task 9's wall-clock deadline covers the whole step rather than each call separately.

**Scope note:** the provisioner writes through `VerificationJobActor.CreateScope`, not the traveler's request scope. `AdminAuditInterceptor` audits every tracked change in a `SaveChanges` that carries an actor, so setting an actor on the traveler's own scope would begin auditing ordinary traveler flight writes — which CLAUDE.md says must stay untouched. It passes no correlation id: the request's id lives in `HttpContext.Items`, which Application cannot see, so these audit rows fall back to `Activity.Current?.Id` like any other actor-less path. That is a known, accepted gap.

- [ ] **Step 1: Write the failing tests**

Create `FlightAssistant.Application.Tests/ReferenceData/ReferenceDataProvisionerTests.cs`:

```csharp
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.ReferenceData;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlightAssistant.Application.Tests.ReferenceData;

public class ReferenceDataProvisionerTests
{
    private readonly IAirportRepository _airports = Substitute.For<IAirportRepository>();
    private readonly IAirlineRepository _airlines = Substitute.For<IAirlineRepository>();
    private readonly ICountryRepository _countries = Substitute.For<ICountryRepository>();
    private readonly IVerificationSourceRegistry _registry = Substitute.For<IVerificationSourceRegistry>();
    private readonly ICountryNameResolver _countryNames = Substitute.For<ICountryNameResolver>();
    private readonly ICacheService _cache = Substitute.For<ICacheService>();
    private readonly IProviderBudgetGuard _budget = Substitute.For<IProviderBudgetGuard>();

    private sealed class Options : IReferenceProvisioningOptions
    {
        public bool Enabled { get; set; } = true;
        public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan NegativeCacheTtl { get; set; } = TimeSpan.FromHours(24);
    }

    // Read IVerificationJobOptions and implement every member it declares — the list below is
    // what it had when this plan was written, and the compiler will tell you if it has since
    // grown. Only DefaultSourcesFor is actually exercised here.
    private sealed class JobOptions : IVerificationJobOptions
    {
        public bool Enabled => true;
        public TimeSpan PollInterval => TimeSpan.FromSeconds(30);
        public int BatchSize => 10;
        public int MaxAttempts => 3;
        public TimeSpan IdleRelease => TimeSpan.FromMinutes(30);
        public int StaleAfterDays => 180;
        public TimeSpan ErrorCooldown => TimeSpan.FromHours(6);
        public TimeSpan ClaimPromotionAge => TimeSpan.FromMinutes(10);

        public IReadOnlyList<string> DefaultSourcesFor(VerificationEntityType entityType) =>
            entityType switch
            {
                VerificationEntityType.Airport => ["ourairports"],
                VerificationEntityType.Airline => ["aeroapi"],
                _ => ["mledoze"]
            };
    }

    private readonly Options _options = new();

    /// <summary>A stand-in source whose answers the test dictates, with a cost so the metered
    /// gate can be exercised.</summary>
    private sealed class FakeSource(
        string name,
        decimal cost,
        VerificationEntityType[] supports,
        Func<VerificationSourceLookupKey, VerificationSourceFetchResult> answer) : IVerificationSource
    {
        public int Calls { get; private set; }
        public string Name => name;
        public decimal CostPerRowUsd => cost;
        public IReadOnlyCollection<VerificationEntityType> SupportedEntityTypes => supports;

        public Task<VerificationSourceFetchResult> LookupAsync(
            VerificationEntityType entityType, VerificationSourceLookupKey key, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(answer(key));
        }

        public void ReleaseCache(TimeSpan idleRelease) { }
        public bool HasCachedData => false;
    }

    private static VerificationSourceFetchResult FoundAirport(string iata) =>
        VerificationSourceFetchResult.Found(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(Airport.Iata)] = iata,
            [nameof(Airport.Icao)] = "ZZZZ",
            [nameof(Airport.Name)] = "Test Airport",
            [nameof(Airport.City)] = "Testville",
            [nameof(Airport.CountryCode)] = "GB",
            [nameof(Airport.Latitude)] = "51.5",
            [nameof(Airport.Longitude)] = "-0.45",
            [nameof(Airport.ElevationFt)] = "83"
        });

    /// <summary>Builds the provisioner over a real ServiceProvider, because it resolves its
    /// repositories from a scope of its own rather than taking them as constructor dependencies.</summary>
    private ReferenceDataProvisioner CreateProvisioner()
    {
        var services = new ServiceCollection();
        services.AddScoped<AdminContext>();
        services.AddScoped<IAdminContext>(sp => sp.GetRequiredService<AdminContext>());
        services.AddScoped(_ => _airports);
        services.AddScoped(_ => _airlines);
        services.AddScoped(_ => _countries);
        var provider = services.BuildServiceProvider();

        return new ReferenceDataProvisioner(
            _registry,
            new JobOptions(),
            _countryNames,
            _cache,
            _budget,
            _options,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReferenceDataProvisioner>.Instance);
    }

    public ReferenceDataProvisionerTests()
    {
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Normal);
        _countries.FindByCodeAsync("GB", Arg.Any<CancellationToken>())
            .Returns(Country.Create("GB", "United Kingdom"));
    }

    [Fact]
    public async Task UnknownAirport_ThatTheSourceHas_IsCreatedAndVerified()
    {
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR"));
        _registry.TryGet("ourairports").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _airports.Received(1).AddAsync(
            Arg.Is<Airport>(a =>
                a.Iata == "LHR" &&
                a.Name == "Test Airport" &&
                a.City == "Testville" &&
                a.CountryCode == "GB" &&
                a.IsVerified &&
                a.VerifiedBy == VerificationJobActor.ActorId &&
                a.VerificationSource == "ourairports" &&
                a.SourceType == "auto"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownAirport_ThatTheSourceDoesNotHave_IsNotFound()
    {
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport],
            _ => VerificationSourceFetchResult.NotFound);
        _registry.TryGet("ourairports").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "ZZZ")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(VerificationEntityType.Airport, result.FailedType);
        Assert.Equal("ZZZ", result.FailedCode);
        await _airports.DidNotReceive().AddAsync(Arg.Any<Airport>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SourceError_IsUnavailable_NotNotFound()
    {
        // The distinction is the whole point: a failing source must never be reported to a traveler
        // as "this airport does not exist".
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport],
            _ => VerificationSourceFetchResult.SourceError("ourairports download failed: HTTP 503"));
        _registry.TryGet("ourairports").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
        Assert.Equal("ourairports download failed: HTTP 503", result.DiagnosticDetail);
    }

    [Fact]
    public async Task Disabled_ReportsNotFoundWithoutTouchingASource()
    {
        _options.Enabled = false;
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR"));
        _registry.TryGet("ourairports").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task UnregisteredSource_IsUnavailable_NotNotFound()
    {
        // A misconfiguration is not evidence the entity is absent.
        _registry.TryGet("ourairports").Returns((IVerificationSource?)null);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task NothingMissing_IsProvisionedAndWritesNothing()
    {
        var result = await CreateProvisioner().ProvisionAsync([], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _airports.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceDataProvisionerTests"
```

Expected: FAIL to compile — the contracts and the provisioner do not exist.

- [ ] **Step 3: Add the contracts**

Create `FlightAssistant.Application/ReferenceData/ReferenceProvisionContracts.cs`:

```csharp
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.ReferenceData
{
    /// <summary>One reference row a caller could not resolve: its type and the code it looked it
    /// up by (an IATA code for an airport or airline, a two-letter code for a country).</summary>
    public sealed record MissingReference(VerificationEntityType Type, string Code);

    /// <summary>What a provisioning attempt achieved (#465).</summary>
    public enum ReferenceProvisionStatus
    {
        /// <summary>Every missing row now exists. The caller re-resolves its ids and continues.</summary>
        Provisioned,

        /// <summary>A source answered, and it has no such entity. The only outcome that may become a
        /// 404 -- the caller genuinely learned the entity does not exist.</summary>
        NotFound,

        /// <summary>We could not find out: a source failed, the budget held the call back, the
        /// wall-clock deadline ran out, or the source is not registered. Becomes a 503, never a 404.</summary>
        Unavailable
    }

    /// <summary>
    /// The outcome of one <see cref="IReferenceDataProvisioner.ProvisionAsync"/> call, naming the
    /// entity that failed so the caller's 404 or 503 can say which one it was.
    /// </summary>
    public sealed record ReferenceProvisionResult(
        ReferenceProvisionStatus Status,
        VerificationEntityType? FailedType,
        string? FailedCode,
        string? DiagnosticDetail)
    {
        public static readonly ReferenceProvisionResult Provisioned =
            new(ReferenceProvisionStatus.Provisioned, null, null, null);

        public static ReferenceProvisionResult NotFound(VerificationEntityType type, string code) =>
            new(ReferenceProvisionStatus.NotFound, type, code, null);

        /// <param name="diagnosticDetail">An operator-facing cause -- a source's own fixed-category
        /// error, or the budget state. Surfaced outside Production only, per the
        /// <c>DomainException.DiagnosticDetail</c> convention, so it must never carry a credential
        /// or a request URL.</param>
        public static ReferenceProvisionResult Unavailable(
            VerificationEntityType type, string code, string? diagnosticDetail) =>
            new(ReferenceProvisionStatus.Unavailable, type, code, diagnosticDetail);
    }

    /// <summary>
    /// Creates reference rows that are missing locally but exist in the external sources we already
    /// use for verification (#465), so <c>POST /flights</c> can track a flight through an airport or
    /// on an airline nobody has imported yet, and 404 means the entity genuinely does not exist.
    /// </summary>
    public interface IReferenceDataProvisioner
    {
        /// <summary>
        /// Resolves every entry in <paramref name="missing"/>, plus any country a resolved airport or
        /// airline turns out to need, and writes them in one save. Never throws for an expected
        /// failure -- a source error, a miss, a budget refusal and a timeout are all statuses.
        /// </summary>
        Task<ReferenceProvisionResult> ProvisionAsync(
            IReadOnlyCollection<MissingReference> missing, CancellationToken ct = default);
    }
}
```

- [ ] **Step 4: Add the provisioner, airports only**

Create `FlightAssistant.Application/ReferenceData/ReferenceDataProvisioner.cs`:

```csharp
using System.Globalization;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.ReferenceData
{
    /// <summary>
    /// Creates reference rows from the verification sources (#465). See
    /// <see cref="IReferenceDataProvisioner"/> for what it promises; this type owns how.
    ///
    /// <para>
    /// <b>Its own DI scope.</b> Writes go through <see cref="VerificationJobActor.CreateScope"/>,
    /// not the caller's scope. <c>AdminAuditInterceptor</c> audits every tracked change in a
    /// <c>SaveChanges</c> that carries an actor, so setting one on a traveler's request scope would
    /// start auditing that traveler's own flight writes -- which must stay untouched. A separate
    /// scope audits the reference writes under <c>system</c> and leaves the traveler's write alone.
    /// It passes no correlation id: the request's id lives in <c>HttpContext.Items</c>, invisible to
    /// Application, so these rows fall back to <c>Activity.Current?.Id</c> like any actor-less path.
    /// </para>
    ///
    /// <para>
    /// <b>It never throws for an expected failure.</b> A miss, a source error, a budget refusal and
    /// a deadline are statuses, because this runs inside a traveler's <c>POST /flights</c> and the
    /// caller has to turn each into a different status code.
    /// </para>
    /// </summary>
    public sealed class ReferenceDataProvisioner : IReferenceDataProvisioner
    {
        /// <summary><c>ISeedableEntity.SourceType</c> for a row this service created -- distinct from
        /// <c>seed</c>, <c>admin</c> and <c>import</c>, so an operator can tell where it came from.
        /// The <c>Create</c> factories hardcode <c>"admin"</c>, so it is assigned afterwards.</summary>
        public const string ProvisionedSourceType = "auto";

        private readonly IVerificationSourceRegistry _registry;
        private readonly IVerificationJobOptions _verificationOptions;
        private readonly ICountryNameResolver _countryNames;
        private readonly ICacheService _cache;
        private readonly IProviderBudgetGuard _budget;
        private readonly IReferenceProvisioningOptions _options;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ReferenceDataProvisioner> _logger;

        public ReferenceDataProvisioner(
            IVerificationSourceRegistry registry,
            IVerificationJobOptions verificationOptions,
            ICountryNameResolver countryNames,
            ICacheService cache,
            IProviderBudgetGuard budget,
            IReferenceProvisioningOptions options,
            IServiceScopeFactory scopeFactory,
            ILogger<ReferenceDataProvisioner> logger)
        {
            _registry = registry;
            _verificationOptions = verificationOptions;
            _countryNames = countryNames;
            _cache = cache;
            _budget = budget;
            _options = options;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<ReferenceProvisionResult> ProvisionAsync(
            IReadOnlyCollection<MissingReference> missing, CancellationToken ct = default)
        {
            if (missing.Count == 0)
                return ReferenceProvisionResult.Provisioned;

            // Disabled behaves exactly like "the source has no such entity", which is today's 404.
            if (!_options.Enabled)
            {
                var first = missing.First();
                return ReferenceProvisionResult.NotFound(first.Type, first.Code);
            }

            using var scope = VerificationJobActor.CreateScope(_scopeFactory);
            var airports = scope.ServiceProvider.GetRequiredService<IAirportRepository>();

            var created = new List<Airport>();
            foreach (var entry in missing.Where(m => m.Type == VerificationEntityType.Airport))
            {
                var fetch = await FetchAsync(entry.Type, entry.Code, ct).ConfigureAwait(false);
                if (fetch.Status != FetchStatus.Found)
                    return fetch.ToProvisionResult(entry.Type, entry.Code);

                created.Add(BuildAirport(entry.Code, fetch.SourceName!, fetch.Values!));
            }

            foreach (var airport in created)
                await airports.AddAsync(airport, ct).ConfigureAwait(false);

            if (created.Count > 0)
                await airports.SaveChangesAsync(ct).ConfigureAwait(false);

            return ReferenceProvisionResult.Provisioned;
        }

        private static Airport BuildAirport(
            string requestedIata, string sourceName, IReadOnlyDictionary<string, string?> values)
        {
            var airport = Airport.Create(
                iata: Value(values, nameof(Airport.Iata)) ?? requestedIata,
                icao: Value(values, nameof(Airport.Icao)),
                name: Value(values, nameof(Airport.Name)) ?? requestedIata,
                city: Value(values, nameof(Airport.City)) ?? string.Empty,
                countryCode: Value(values, nameof(Airport.CountryCode))!,
                latitude: ParseDouble(Value(values, nameof(Airport.Latitude))),
                longitude: ParseDouble(Value(values, nameof(Airport.Longitude))),
                elevationFt: ParseInt(Value(values, nameof(Airport.ElevationFt))));

            Stamp(airport, sourceName);
            return airport;
        }

        /// <summary>Marks a freshly-created row as verified by the source it came from. The row *is*
        /// that source's data, so it is verified by construction; stamping it also stops the
        /// flight-tracking trigger (#410) re-queueing it for <c>Verification:StaleAfterDays</c>.</summary>
        private static void Stamp(SeedableEntity entity, string sourceName)
        {
            entity.SourceType = ProvisionedSourceType;
            entity.Verify(VerificationJobActor.ActorId, sourceName);
        }

        private static string? Value(IReadOnlyDictionary<string, string?> values, string column) =>
            values.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        private static double? ParseDouble(string? value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        private static int? ParseInt(string? value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        private enum FetchStatus { Found, NotFound, Unavailable }

        private sealed record Fetch(
            FetchStatus Status, string? SourceName, IReadOnlyDictionary<string, string?>? Values, string? Error)
        {
            public static readonly Fetch NotFound = new(FetchStatus.NotFound, null, null, null);
            public static Fetch Found(string sourceName, IReadOnlyDictionary<string, string?> values) =>
                new(FetchStatus.Found, sourceName, values, null);
            public static Fetch Unavailable(string error) => new(FetchStatus.Unavailable, null, null, error);

            public ReferenceProvisionResult ToProvisionResult(VerificationEntityType type, string code) =>
                Status == FetchStatus.NotFound
                    ? ReferenceProvisionResult.NotFound(type, code)
                    : ReferenceProvisionResult.Unavailable(type, code, Error);
        }

        /// <summary>
        /// Asks each source configured for <paramref name="type"/> in turn; the first that answers
        /// Found wins. A source error stops the search rather than falling through to the next
        /// source: falling through would let one broken source's outage be reported as a miss.
        /// </summary>
        private async Task<Fetch> FetchAsync(VerificationEntityType type, string code, CancellationToken ct)
        {
            var sourceNames = _verificationOptions.DefaultSourcesFor(type);
            if (sourceNames.Count == 0)
                return Fetch.Unavailable($"No source is configured for {type}.");

            foreach (var sourceName in sourceNames)
            {
                var source = _registry.TryGet(sourceName);
                if (source is null)
                    return Fetch.Unavailable($"Source '{sourceName}' is not registered.");

                if (!source.SupportedEntityTypes.Contains(type))
                    continue;

                var key = type switch
                {
                    VerificationEntityType.Airport => VerificationSourceLookupKey.ForAirport(code, null),
                    VerificationEntityType.Airline => VerificationSourceLookupKey.ForAirline(code, null),
                    VerificationEntityType.Country => VerificationSourceLookupKey.ForCountry(code),
                    _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown entity type.")
                };

                var result = await source.LookupAsync(type, key, ct).ConfigureAwait(false);
                if (result.IsError)
                {
                    _logger.LogWarning(
                        "Provisioning {EntityType} '{Code}' from {SourceName} failed: {Error}",
                        type, code, sourceName, result.Error);
                    return Fetch.Unavailable(result.Error);
                }

                if (result.Lookup!.IsFound)
                    return Fetch.Found(sourceName, result.Lookup.Values!);
            }

            return Fetch.NotFound;
        }
    }
}
```

- [ ] **Step 5: Register it**

In `FlightAssistant.Application/DependencyInjection.cs`, beside the other `AddScoped` registrations:

```csharp
            services.AddScoped<IReferenceDataProvisioner, ReferenceDataProvisioner>();
```

- [ ] **Step 6: Run the tests to verify they pass**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceDataProvisionerTests"
```

Expected: PASS.

- [ ] **Step 7: Commit**

Stage the contracts, the provisioner, `DependencyInjection.cs` and the test, then commit with message:

```
feat(reference-data): provision a missing airport from its verification source (#465)
```

---

## Task 5: Countries, and foreign-key ordering

**Files:**
- Modify: `FlightAssistant.Application/ReferenceData/ReferenceDataProvisioner.cs`
- Test: `FlightAssistant.Application.Tests/ReferenceData/ReferenceDataProvisionerTests.cs`

**Interfaces:**
- Consumes: everything Task 4 produced.
- Produces: no new public types. Task 6 relies on the private `EnsureCountriesAsync(ICountryRepository, IReadOnlyCollection<string>, Action, CancellationToken) -> Task<ReferenceProvisionResult?>` helper added here (null means "all present").

**Why:** `Airport.CountryCode` is a foreign key into `countries`, so a provisioned airport in a country we do not hold would fail its constraint at `SaveChanges`. The country a row needs is only known *after* the source answers, so countries are resolved second and written first.

- [ ] **Step 1: Write the failing tests**

Append to `ReferenceDataProvisionerTests.cs`:

```csharp
    private static VerificationSourceFetchResult FoundCountry(string code, string name) =>
        VerificationSourceFetchResult.Found(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(Country.Code)] = code,
            [nameof(Country.Name)] = name,
            [nameof(Country.Alpha3)] = "GBR",
            [nameof(Country.NumericCode)] = "826",
            [nameof(Country.Region)] = "Europe",
            [nameof(Country.Subregion)] = "Northern Europe"
        });

    [Fact]
    public async Task AirportInAnUnknownCountry_ProvisionsTheCountryToo()
    {
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));
        _registry.TryGet("mledoze").Returns(
            new FakeSource("mledoze", 0m, [VerificationEntityType.Country], _ => FoundCountry("GB", "United Kingdom")));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _countries.Received(1).AddAsync(
            Arg.Is<Country>(c =>
                c.Code == "GB" &&
                c.Name == "United Kingdom" &&
                c.Alpha3 == "GBR" &&
                c.NumericCode == 826 &&
                c.IsVerified &&
                c.VerificationSource == "mledoze"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AirportInAKnownCountry_DoesNotTouchTheCountrySource()
    {
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([Country.Create("GB", "United Kingdom")]);
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));
        var countrySource = new FakeSource("mledoze", 0m, [VerificationEntityType.Country],
            _ => FoundCountry("GB", "United Kingdom"));
        _registry.TryGet("mledoze").Returns(countrySource);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        Assert.Equal(0, countrySource.Calls);
        await _countries.DidNotReceive().AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoAirportsInOneCountry_ResolveThatCountryOnce()
    {
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], key => FoundAirport(key.Iata!)));
        var countrySource = new FakeSource("mledoze", 0m, [VerificationEntityType.Country],
            _ => FoundCountry("GB", "United Kingdom"));
        _registry.TryGet("mledoze").Returns(countrySource);

        await CreateProvisioner().ProvisionAsync(
            [
                new MissingReference(VerificationEntityType.Airport, "LHR"),
                new MissingReference(VerificationEntityType.Airport, "LGW")
            ],
            CancellationToken.None);

        Assert.Equal(1, countrySource.Calls);
        await _countries.Received(1).AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AirportWhoseCountryTheSourceDoesNotHave_IsNotFoundOnTheCountry()
    {
        // The airport exists but we cannot satisfy its foreign key, so the failure is reported
        // against the country — that is the row that is genuinely absent everywhere.
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));
        _registry.TryGet("mledoze").Returns(
            new FakeSource("mledoze", 0m, [VerificationEntityType.Country],
                _ => VerificationSourceFetchResult.NotFound));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(VerificationEntityType.Country, result.FailedType);
        Assert.Equal("GB", result.FailedCode);
        await _airports.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACountryAskedForDirectly_IsProvisioned()
    {
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _registry.TryGet("mledoze").Returns(
            new FakeSource("mledoze", 0m, [VerificationEntityType.Country], _ => FoundCountry("GB", "United Kingdom")));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Country, "GB")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _countries.Received(1).AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceDataProvisionerTests.AirportInAnUnknownCountry"
```

Expected: FAIL — no country is provisioned, so `AddAsync` is never received.

- [ ] **Step 3: Resolve countries before writing**

In `ReferenceDataProvisioner.cs`, replace the body of `ProvisionAsync` from the `using var scope` line to the `return ReferenceProvisionResult.Provisioned;` line with:

```csharp
            using var scope = VerificationJobActor.CreateScope(_scopeFactory);
            var airports = scope.ServiceProvider.GetRequiredService<IAirportRepository>();
            var countries = scope.ServiceProvider.GetRequiredService<ICountryRepository>();

            var createdAirports = new List<Airport>();

            // Case-insensitive: a source may report "gb" where another reports "GB", and the country
            // code is canonicalised by Country.Create anyway.
            var requiredCountryCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wroteAnything = false;

            foreach (var entry in missing.Where(m => m.Type == VerificationEntityType.Airport))
            {
                var fetch = await FetchAsync(entry.Type, entry.Code, ct).ConfigureAwait(false);
                if (fetch.Status != FetchStatus.Found)
                    return fetch.ToProvisionResult(entry.Type, entry.Code);

                var airport = BuildAirport(entry.Code, fetch.SourceName!, fetch.Values!);
                createdAirports.Add(airport);
                requiredCountryCodes.Add(airport.CountryCode);
            }

            foreach (var entry in missing.Where(m => m.Type == VerificationEntityType.Country))
                requiredCountryCodes.Add(entry.Code);

            // Countries first: airports.CountryCode is a foreign key into countries, and the country a
            // row needs is only known once its source has answered.
            var countryFailure = await EnsureCountriesAsync(
                countries, requiredCountryCodes, () => wroteAnything = true, ct).ConfigureAwait(false);
            if (countryFailure is not null)
                return countryFailure;

            foreach (var airport in createdAirports)
            {
                await airports.AddAsync(airport, ct).ConfigureAwait(false);
                wroteAnything = true;
            }

            // Every repository resolved from this scope shares its DbContext, so one save commits
            // every row in a single transaction, and EF orders the INSERTs by the foreign-key graph.
            // ReferenceProvisioningPostgresTests proves that ordering against real PostgreSQL, which
            // the InMemory provider cannot (it enforces no foreign key at all).
            if (wroteAnything)
                await countries.SaveChangesAsync(ct).ConfigureAwait(false);

            return ReferenceProvisionResult.Provisioned;
```

- [ ] **Step 4: Add the country helpers**

Add to `ReferenceDataProvisioner.cs`, beside `BuildAirport`:

```csharp
        /// <summary>
        /// Creates any of <paramref name="codes"/> that <c>countries</c> does not already hold.
        /// Returns null when every code is now present, or the failure that stopped it. One batched
        /// read for the whole set, then at most one source lookup per genuinely missing code -- so
        /// two airports in the same country cost one lookup, not two.
        /// </summary>
        private async Task<ReferenceProvisionResult?> EnsureCountriesAsync(
            ICountryRepository countries,
            IReadOnlyCollection<string> codes,
            Action markWrote,
            CancellationToken ct)
        {
            if (codes.Count == 0)
                return null;

            var existing = await countries.FindByCodesAsync(codes, ct).ConfigureAwait(false);
            var present = existing.Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var code in codes)
            {
                if (present.Contains(code))
                    continue;

                var fetch = await FetchAsync(VerificationEntityType.Country, code, ct).ConfigureAwait(false);
                if (fetch.Status != FetchStatus.Found)
                    return fetch.ToProvisionResult(VerificationEntityType.Country, code);

                await countries.AddAsync(BuildCountry(code, fetch.SourceName!, fetch.Values!), ct).ConfigureAwait(false);
                markWrote();
            }

            return null;
        }

        private static Country BuildCountry(
            string requestedCode, string sourceName, IReadOnlyDictionary<string, string?> values)
        {
            var country = Country.Create(
                code: Value(values, nameof(Country.Code)) ?? requestedCode,
                name: Value(values, nameof(Country.Name)) ?? requestedCode,
                alpha3: Value(values, nameof(Country.Alpha3)),
                numericCode: ParseInt(Value(values, nameof(Country.NumericCode))),
                region: Value(values, nameof(Country.Region)),
                subregion: Value(values, nameof(Country.Subregion)));

            Stamp(country, sourceName);
            return country;
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceDataProvisionerTests"
```

Expected: PASS, Task 4's tests included.

- [ ] **Step 6: Commit**

Stage the provisioner and the test file, then commit with message:

```
feat(reference-data): provision an airport's country before the airport (#465)
```

---

## Task 6: Airlines, and the country-name resolution

**Files:**
- Modify: `FlightAssistant.Application/ReferenceData/ReferenceDataProvisioner.cs`
- Test: `FlightAssistant.Application.Tests/ReferenceData/ReferenceDataProvisionerTests.cs`

**Interfaces:**
- Consumes: `ICountryNameResolver` and `CountryCodeResolution` (Task 1); `VerificationExtraValues.AirlineCountryName` (Task 2); everything from Tasks 4–5.
- Produces: no new public types.

**Why:** `Airline.Create` requires a `CountryCode` foreign key, and `aeroapi` reports only a country *name*. An unresolvable name means no valid row can be built, so that is a 404 against the airline — it is a data gap we cannot fill, not a transient failure. A resolver *error*, by contrast, is a 503.

- [ ] **Step 1: Write the failing tests**

Append to `ReferenceDataProvisionerTests.cs`:

```csharp
    private static VerificationSourceFetchResult FoundAirline(string iata, string? countryName) =>
        VerificationSourceFetchResult.Found(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(Airline.Iata)] = iata,
            [nameof(Airline.Icao)] = "BAW",
            [nameof(Airline.Name)] = "British Airways",
            [VerificationExtraValues.AirlineCountryName] = countryName
        });

    [Fact]
    public async Task UnknownAirline_ResolvesItsCountryNameToACode()
    {
        _registry.TryGet("aeroapi").Returns(
            new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline], _ => FoundAirline("BA", "United Kingdom")));
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([Country.Create("GB", "United Kingdom")]);
        _countryNames.TryResolveCodeByNameAsync("United Kingdom", Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.Found("GB"));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _airlines.Received(1).AddAsync(
            Arg.Is<Airline>(a =>
                a.Iata == "BA" &&
                a.Icao == "BAW" &&
                a.Name == "British Airways" &&
                a.CountryCode == "GB" &&
                a.IsVerified &&
                a.VerificationSource == "aeroapi" &&
                a.SourceType == "auto"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownAirline_WhoseCountryNameCannotBeResolved_IsNotFound()
    {
        // No country code means no valid Airline row — the FK is required. That is a data gap, not
        // a transient failure, so it is a 404 rather than a 503.
        _registry.TryGet("aeroapi").Returns(
            new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline], _ => FoundAirline("BA", "Atlantis")));
        _countryNames.TryResolveCodeByNameAsync("Atlantis", Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.NotFound);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(VerificationEntityType.Airline, result.FailedType);
        Assert.Equal("BA", result.FailedCode);
        await _airlines.DidNotReceive().AddAsync(Arg.Any<Airline>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownAirline_WhenTheResolverFails_IsUnavailable()
    {
        _registry.TryGet("aeroapi").Returns(
            new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline], _ => FoundAirline("BA", "United Kingdom")));
        _countryNames.TryResolveCodeByNameAsync("United Kingdom", Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.Failed("mledoze download failed: HTTP 503"));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
        Assert.Equal("mledoze download failed: HTTP 503", result.DiagnosticDetail);
    }

    [Fact]
    public async Task UnknownAirline_WithNoCountryNameAtAll_IsNotFound()
    {
        _registry.TryGet("aeroapi").Returns(
            new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline], _ => FoundAirline("BA", null)));
        _countryNames.TryResolveCodeByNameAsync(null, Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.NotFound);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(VerificationEntityType.Airline, result.FailedType);
    }

    [Fact]
    public async Task UnknownAirline_InAnUnknownCountry_ProvisionsTheCountryToo()
    {
        _registry.TryGet("aeroapi").Returns(
            new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline], _ => FoundAirline("BA", "United Kingdom")));
        _registry.TryGet("mledoze").Returns(
            new FakeSource("mledoze", 0m, [VerificationEntityType.Country], _ => FoundCountry("GB", "United Kingdom")));
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _countryNames.TryResolveCodeByNameAsync("United Kingdom", Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.Found("GB"));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        await _countries.Received(1).AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceDataProvisionerTests.UnknownAirline"
```

Expected: FAIL — airlines are not provisioned at all yet.

- [ ] **Step 3: Provision airlines**

In `ProvisionAsync`, resolve the airline repository next to the other two:

```csharp
            var airlines = scope.ServiceProvider.GetRequiredService<IAirlineRepository>();
```

Immediately after the airport loop and before the explicit-country loop, add:

```csharp
            var createdAirlines = new List<Airline>();
            foreach (var entry in missing.Where(m => m.Type == VerificationEntityType.Airline))
            {
                var fetch = await FetchAsync(entry.Type, entry.Code, ct).ConfigureAwait(false);
                if (fetch.Status != FetchStatus.Found)
                    return fetch.ToProvisionResult(entry.Type, entry.Code);

                // aeroapi reports an operator's country as a name, never a code (#401 R1), but
                // Airline.CountryCode is a required foreign key — so the name has to be resolved
                // before a row can exist at all.
                var countryName = Value(fetch.Values!, VerificationExtraValues.AirlineCountryName);
                var resolution = await _countryNames
                    .TryResolveCodeByNameAsync(countryName, ct).ConfigureAwait(false);

                if (resolution.IsError)
                    return ReferenceProvisionResult.Unavailable(entry.Type, entry.Code, resolution.Error);

                if (resolution.Code is null)
                {
                    // No code means no valid row: a data gap we cannot fill, not a transient
                    // failure, so the caller answers 404 rather than 503.
                    _logger.LogInformation(
                        "Cannot provision airline '{Code}': its country '{CountryName}' did not resolve to a code.",
                        entry.Code, countryName);
                    return ReferenceProvisionResult.NotFound(entry.Type, entry.Code);
                }

                createdAirlines.Add(BuildAirline(entry.Code, resolution.Code, fetch.SourceName!, fetch.Values!));
                requiredCountryCodes.Add(resolution.Code);
            }
```

Then, alongside the airport write loop:

```csharp
            foreach (var airline in createdAirlines)
            {
                await airlines.AddAsync(airline, ct).ConfigureAwait(false);
                wroteAnything = true;
            }
```

- [ ] **Step 4: Add `BuildAirline`**

Beside `BuildAirport`:

```csharp
        private static Airline BuildAirline(
            string requestedIata,
            string countryCode,
            string sourceName,
            IReadOnlyDictionary<string, string?> values)
        {
            var airline = Airline.Create(
                iata: Value(values, nameof(Airline.Iata)) ?? requestedIata,
                icao: Value(values, nameof(Airline.Icao)),
                name: Value(values, nameof(Airline.Name)) ?? requestedIata,
                countryCode: countryCode);

            Stamp(airline, sourceName);
            return airline;
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceDataProvisionerTests"
```

Expected: PASS, every earlier test included.

- [ ] **Step 6: Commit**

Stage the provisioner and the test file, then commit with message:

```
feat(reference-data): provision a missing airline, resolving its country name (#465)
```

---

## Task 7: The negative cache

**Files:**
- Modify: `FlightAssistant.Application/ReferenceData/ReferenceDataProvisioner.cs`
- Test: `FlightAssistant.Application.Tests/ReferenceData/ReferenceDataProvisionerTests.cs`

**Interfaces:**
- Consumes: `ICacheService` and `IReferenceProvisioningOptions.NegativeCacheTtl`.
- Produces: `ReferenceDataProvisioner.NegativeCacheKeyFor(VerificationEntityType, string) -> string`, `internal static`, so the test names the same key the code writes rather than restating the format.

**Why:** `POST /flights` accepts any well-formed designator; it need not have come from search. An unknown airline triggers a metered `aeroapi` lookup at $0.015 against a $5/month budget, and the `flight-track` limiter allows 10/60 s per caller. Remembering a miss makes a repeated bogus designator free after the first.

- [ ] **Step 1: Write the failing tests**

Append to `ReferenceDataProvisionerTests.cs`:

```csharp
    [Fact]
    public async Task ACachedMiss_SkipsTheSourceEntirely()
    {
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR"));
        _registry.TryGet("ourairports").Returns(source);
        _cache.GetAsync<string>(
                ReferenceDataProvisioner.NegativeCacheKeyFor(VerificationEntityType.Airport, "ZZZ"),
                Arg.Any<CancellationToken>())
            .Returns("1");

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "ZZZ")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task AFreshMiss_IsRemembered()
    {
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport],
                _ => VerificationSourceFetchResult.NotFound));

        await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "ZZZ")], CancellationToken.None);

        await _cache.Received(1).SetAsync(
            ReferenceDataProvisioner.NegativeCacheKeyFor(VerificationEntityType.Airport, "ZZZ"),
            Arg.Any<string>(),
            _options.NegativeCacheTtl,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AHit_IsNeverCached()
    {
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));

        await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        await _cache.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASourceError_IsNeverCached()
    {
        // Caching a failure would turn a brief outage into hours of 404s.
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport],
                _ => VerificationSourceFetchResult.SourceError("ourairports download failed: HTTP 503")));

        await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        await _cache.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ZeroTtl_NeitherReadsNorWritesTheCache()
    {
        _options.NegativeCacheTtl = TimeSpan.Zero;
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport],
                _ => VerificationSourceFetchResult.NotFound));

        await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "ZZZ")], CancellationToken.None);

        await _cache.DidNotReceive().GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheCacheKey_IsCaseInsensitiveOnTheCode()
    {
        Assert.Equal(
            ReferenceDataProvisioner.NegativeCacheKeyFor(VerificationEntityType.Airport, "zzz"),
            ReferenceDataProvisioner.NegativeCacheKeyFor(VerificationEntityType.Airport, "ZZZ"));
    }
```

Make `FlightAssistant.Application`'s internals visible to the test project if they are not already — check for an existing `InternalsVisibleTo` in `FlightAssistant.Application.csproj`. If there is none, make `NegativeCacheKeyFor` `public` instead of `internal` rather than adding the attribute; it is a pure function and exposing it is harmless.

- [ ] **Step 2: Run the tests to verify they fail**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceDataProvisionerTests.ACachedMiss"
```

Expected: FAIL to compile — `NegativeCacheKeyFor` does not exist.

- [ ] **Step 3: Add the cache around the fetch**

In `ReferenceDataProvisioner.cs`, add the key helper beside the other statics:

```csharp
        /// <summary>
        /// The cache key remembering that a source has no such entity. Upper-cased so a caller's
        /// casing cannot produce a second entry for the same code -- <c>POST /flights</c> does not
        /// canonicalise the designator before the lookup.
        /// </summary>
        public static string NegativeCacheKeyFor(VerificationEntityType type, string code) =>
            $"refdata:missing:{type}:{code.Trim().ToUpperInvariant()}";
```

Then wrap `FetchAsync`. Rename the existing method to `FetchFromSourcesAsync` (its body is unchanged) and add this in front of it:

```csharp
        /// <summary>
        /// <see cref="FetchFromSourcesAsync"/>, with the negative cache in front of it: a code a
        /// source has already said it does not have costs nothing to ask about again. Only a miss is
        /// remembered -- never a hit, and never a failure, which would turn a brief outage into
        /// hours of 404s. A cache outage fails open, exactly as the manual-sync cooldown does (#369).
        /// </summary>
        private async Task<Fetch> FetchAsync(VerificationEntityType type, string code, CancellationToken ct)
        {
            var ttl = _options.NegativeCacheTtl;
            var cacheKey = NegativeCacheKeyFor(type, code);

            if (ttl > TimeSpan.Zero)
            {
                try
                {
                    if (await _cache.GetAsync<string>(cacheKey, ct).ConfigureAwait(false) is not null)
                        return Fetch.NotFound;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fail open: a cache outage must not stop a provision that would have succeeded.
                    _logger.LogWarning(ex, "Reading the reference-provisioning negative cache failed for {CacheKey}.", cacheKey);
                }
            }

            var fetch = await FetchFromSourcesAsync(type, code, ct).ConfigureAwait(false);

            if (fetch.Status == FetchStatus.NotFound && ttl > TimeSpan.Zero)
            {
                try
                {
                    await _cache.SetAsync(cacheKey, "1", ttl, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Writing the reference-provisioning negative cache failed for {CacheKey}.", cacheKey);
                }
            }

            return fetch;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceDataProvisionerTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

Stage the provisioner and the test file, then commit with message:

```
feat(reference-data): remember a miss so a bogus code is only paid for once (#465)
```

---

## Task 8: The budget guard in front of metered sources

**Files:**
- Modify: `FlightAssistant.Application/ReferenceData/ReferenceDataProvisioner.cs`
- Test: `FlightAssistant.Application.Tests/ReferenceData/ReferenceDataProvisionerTests.cs`

**Interfaces:**
- Consumes: `IProviderBudgetGuard.GetStateAsync` and `ProviderBudgetState` (existing, #303).
- Produces: no new public types.

**Why:** `aeroapi` costs $0.015 a lookup against a $5/month budget. The guard refuses from `SoftLimit` onward, matching `AeroApiFlightSearchBudgetDecorator` — the closest analogue, also traveler-facing and also discretionary spend — rather than #401's `Exhausted`-only rule, which exists for work an admin explicitly asked for. The result is `Unavailable`, so the caller answers 503: at the cap we have not learned whether the entity exists. Free sources are never gated; gating them would refuse work that costs nothing.

- [ ] **Step 1: Write the failing tests**

Append to `ReferenceDataProvisionerTests.cs`:

```csharp
    [Theory]
    [InlineData(ProviderBudgetState.SoftLimit)]
    [InlineData(ProviderBudgetState.Exhausted)]
    public async Task AMeteredSource_IsHeldBackOnceTheBudgetLeavesNormal(ProviderBudgetState state)
    {
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(state);
        var source = new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline],
            _ => FoundAirline("BA", "United Kingdom"));
        _registry.TryGet("aeroapi").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task AMeteredSource_RunsWhileTheBudgetIsNormal()
    {
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Normal);
        var source = new FakeSource("aeroapi", 0.015m, [VerificationEntityType.Airline],
            _ => FoundAirline("BA", "United Kingdom"));
        _registry.TryGet("aeroapi").Returns(source);
        _countries.FindByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([Country.Create("GB", "United Kingdom")]);
        _countryNames.TryResolveCodeByNameAsync("United Kingdom", Arg.Any<CancellationToken>())
            .Returns(CountryCodeResolution.Found("GB"));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airline, "BA")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task AFreeSource_IsNeverGatedByTheBudget()
    {
        // ourairports and mledoze cost nothing; refusing them would withhold work that spends no money.
        _budget.GetStateAsync(Arg.Any<CancellationToken>()).Returns(ProviderBudgetState.Exhausted);
        var source = new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR"));
        _registry.TryGet("ourairports").Returns(source);

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task AFreeSource_NeverEvenReadsTheBudget()
    {
        _registry.TryGet("ourairports").Returns(
            new FakeSource("ourairports", 0m, [VerificationEntityType.Airport], _ => FoundAirport("LHR")));

        await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        await _budget.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceDataProvisionerTests.AMeteredSource"
```

Expected: FAIL — the metered source is called regardless of the budget.

- [ ] **Step 3: Gate the metered lookup**

In `FetchFromSourcesAsync`, immediately after the `SupportedEntityTypes.Contains(type)` check and before the key is built:

```csharp
                // Only a source that actually bills is gated. From SoftLimit onward, matching
                // AeroApiFlightSearchBudgetDecorator -- the closest analogue, also traveler-facing
                // and also discretionary spend -- rather than #401's Exhausted-only rule, which is
                // for work an admin explicitly asked for. Unavailable, never NotFound: a refusal
                // teaches us nothing about whether the entity exists.
                if (source.CostPerRowUsd > 0m)
                {
                    var budgetState = await _budget.GetStateAsync(ct).ConfigureAwait(false);
                    if (budgetState != ProviderBudgetState.Normal)
                    {
                        _logger.LogInformation(
                            "Not provisioning {EntityType} '{Code}' from metered source {SourceName}: budget is {BudgetState}.",
                            type, code, sourceName, budgetState);
                        return Fetch.Unavailable(
                            $"The flight data budget is {budgetState}, so '{sourceName}' was not called.");
                    }
                }
```

- [ ] **Step 4: Run the tests to verify they pass**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceDataProvisionerTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

Stage the provisioner and the test file, then commit with message:

```
feat(reference-data): hold back metered provisioning once the budget leaves Normal (#465)
```

---

## Task 9: One wall-clock budget for the whole step

**Files:**
- Modify: `FlightAssistant.Application/ReferenceData/ReferenceDataProvisioner.cs`
- Test: `FlightAssistant.Application.Tests/ReferenceData/ReferenceDataProvisionerTests.cs`

**Interfaces:**
- Consumes: `IReferenceProvisioningOptions.Budget`.
- Produces: no new public types.

**Why:** a cold `ourairports` load downloads and parses up to 64 MB, and every AeroAPI call waits on the process-wide 12 s throttle, so an ungoverned provision could hang a traveler's request for a minute or more. Exceeding the budget is `Unavailable` (503), never `NotFound`. No background queue is needed to make the retry work: `VerificationDatasetCache` shares one download across callers and a caller's own cancellation cancels only its own *wait*, so the download keeps running and a retry finds it warm. The `when (!ct.IsCancellationRequested)` filter is the same idiom `AdminHealthReportBuilder` uses (#453) to tell our own deadline apart from the caller abandoning the request.

- [ ] **Step 1: Write the failing tests**

Append to `ReferenceDataProvisionerTests.cs`:

```csharp
    /// <summary>A source that never answers within the test's budget.</summary>
    private sealed class StallingSource(string name, VerificationEntityType[] supports) : IVerificationSource
    {
        public string Name => name;
        public decimal CostPerRowUsd => 0m;
        public IReadOnlyCollection<VerificationEntityType> SupportedEntityTypes => supports;

        public async Task<VerificationSourceFetchResult> LookupAsync(
            VerificationEntityType entityType, VerificationSourceLookupKey key, CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return VerificationSourceFetchResult.NotFound;
        }

        public void ReleaseCache(TimeSpan idleRelease) { }
        public bool HasCachedData => false;
    }

    [Fact]
    public async Task ExceedingTheBudget_IsUnavailable_NotNotFound()
    {
        // A timeout must never be reported as "this airport does not exist": we simply ran out of
        // time. The shared download carries on, so the traveler's retry succeeds.
        _options.Budget = TimeSpan.FromMilliseconds(50);
        _registry.TryGet("ourairports").Returns(new StallingSource("ourairports", [VerificationEntityType.Airport]));

        var result = await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
        Assert.Equal(VerificationEntityType.Airport, result.FailedType);
        Assert.Equal("LHR", result.FailedCode);
    }

    [Fact]
    public async Task ExceedingTheBudget_WritesNothing()
    {
        _options.Budget = TimeSpan.FromMilliseconds(50);
        _registry.TryGet("ourairports").Returns(new StallingSource("ourairports", [VerificationEntityType.Airport]));

        await CreateProvisioner().ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        await _airports.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _countries.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheCallersOwnCancellation_Propagates_RatherThanBecomingUnavailable()
    {
        // The traveler abandoned the request. That is not a provisioning outcome, and swallowing it
        // would have us go on assembling a response for a connection that has already gone (#453).
        _options.Budget = TimeSpan.FromSeconds(30);
        _registry.TryGet("ourairports").Returns(new StallingSource("ourairports", [VerificationEntityType.Airport]));
        using var caller = new CancellationTokenSource();
        caller.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvisioner().ProvisionAsync(
                [new MissingReference(VerificationEntityType.Airport, "LHR")], caller.Token));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceDataProvisionerTests.ExceedingTheBudget"
```

Expected: FAIL — the test hangs for 30 seconds and then reports `NotFound`, because no deadline is applied.

- [ ] **Step 3: Apply the deadline**

In `ReferenceDataProvisioner.cs`, rename the existing `ProvisionAsync` to `ProvisionCoreAsync` (keep its body and signature otherwise identical, taking the same `CancellationToken ct`), and add this as the new public entry point:

```csharp
        public async Task<ReferenceProvisionResult> ProvisionAsync(
            IReadOnlyCollection<MissingReference> missing, CancellationToken ct = default)
        {
            if (missing.Count == 0)
                return ReferenceProvisionResult.Provisioned;

            // Disabled behaves exactly like "the source has no such entity", which is today's 404.
            if (!_options.Enabled)
            {
                var disabledFirst = missing.First();
                return ReferenceProvisionResult.NotFound(disabledFirst.Type, disabledFirst.Code);
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_options.Budget);

            try
            {
                return await ProvisionCoreAsync(missing, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our own budget ran out, not the caller abandoning the request. The dataset download
                // this was waiting on is shared and keeps running, so a retry shortly finds it warm.
                var first = missing.First();
                _logger.LogInformation(
                    "Provisioning {EntityType} '{Code}' exceeded the {Budget} budget.",
                    first.Type, first.Code, _options.Budget);
                return ReferenceProvisionResult.Unavailable(
                    first.Type, first.Code,
                    $"Looking up reference data took longer than {_options.Budget}.");
            }
        }
```

Remove the now-duplicated `missing.Count == 0` and `!_options.Enabled` guards from `ProvisionCoreAsync`.

- [ ] **Step 4: Run the tests to verify they pass**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceDataProvisionerTests"
```

Expected: PASS, and the suite finishes in well under 30 seconds.

- [ ] **Step 5: Commit**

Stage the provisioner and the test file, then commit with message:

```
feat(reference-data): bound provisioning with one wall-clock budget (#465)
```

---

## Task 10: Wire the provisioner into `TrackFlightCommandHandler`

**Files:**
- Modify: `FlightAssistant.Application/Commands/Flights/TrackFlightCommand.cs`
- Test: `FlightAssistant.Application.Tests/Commands/Flights/TrackFlightCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `IReferenceDataProvisioner`, `MissingReference`, `ReferenceProvisionStatus` (Tasks 4–9).
- Produces: the behaviour `POST /flights` and the admin track route both inherit — both send `TrackFlightCommand`.

**Why the cap is checked first:** a refused track must still write nothing. This is sound because a missing reference row means no `Flight` row can reference it, so the traveler cannot already be tracking this flight and `AlreadyTracksAsync` would necessarily be false — only the count matters. The existing cap check further down stays exactly as it is and runs for every request; the pre-check costs one extra `COUNT` in the rare provisioning path, which is the price of not writing reference rows for a request that is about to be refused.

- [ ] **Step 1: Write the failing tests**

In `TrackFlightCommandHandlerTests.cs`, add the substitute and a constructor default so the existing 404 tests keep asserting exactly what they assert today:

```csharp
    private readonly IReferenceDataProvisioner _provisioner = Substitute.For<IReferenceDataProvisioner>();

    public TrackFlightCommandHandlerTests()
    {
        // Default: nothing can be provisioned, so every pre-#465 test continues to see its 404.
        _provisioner.ProvisionAsync(Arg.Any<IReadOnlyCollection<MissingReference>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var first = ci.Arg<IReadOnlyCollection<MissingReference>>().First();
                return ReferenceProvisionResult.NotFound(first.Type, first.Code);
            });
    }
```

Pass `_provisioner` to the `TrackFlightCommandHandler` construction wherever `_handler` is built in that file. Then append:

```csharp
    [Fact]
    public async Task EverythingKnown_NeverCallsTheProvisioner()
    {
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        await _handler.Handle(DefaultCmd, CancellationToken.None);

        await _provisioner.DidNotReceive().ProvisionAsync(
            Arg.Any<IReadOnlyCollection<MissingReference>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownAirline_ThatProvisions_Succeeds()
    {
        _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _provisioner.ProvisionAsync(Arg.Any<IReadOnlyCollection<MissingReference>>(), Arg.Any<CancellationToken>())
            .Returns(ReferenceProvisionResult.Provisioned)
            .AndDoes(_ => _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(1));
        _flights.GetOrCreateFlightAsync(Arg.Any<Flight>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Flight>());
        _flights.GetTravelerFlightAsync(DefaultCmd.TravelerId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        await _handler.Handle(DefaultCmd, CancellationToken.None);

        await _provisioner.Received(1).ProvisionAsync(
            Arg.Is<IReadOnlyCollection<MissingReference>>(m =>
                m.Count == 1 &&
                m.Single().Type == VerificationEntityType.Airline &&
                m.Single().Code == "BA"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EveryMissingReference_IsSentInOneCall()
    {
        _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _refData.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).ReturnsNull();
        _refData.GetAirportIdByIataAsync("JFK", Arg.Any<CancellationToken>()).ReturnsNull();

        await Assert.ThrowsAsync<DomainException>(() => _handler.Handle(DefaultCmd, CancellationToken.None));

        await _provisioner.Received(1).ProvisionAsync(
            Arg.Is<IReadOnlyCollection<MissingReference>>(m => m.Count == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownAirline_ThatTheSourceDoesNotHave_Still404sWithTheSameMessage()
    {
        _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(DefaultCmd, CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("Airline 'BA' not found.", ex.Message);
    }

    [Fact]
    public async Task WhenProvisioningIsUnavailable_The503CarriesTheCauseAsDiagnosticDetail()
    {
        _refData.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).ReturnsNull();
        _provisioner.ProvisionAsync(Arg.Any<IReadOnlyCollection<MissingReference>>(), Arg.Any<CancellationToken>())
            .Returns(ReferenceProvisionResult.Unavailable(
                VerificationEntityType.Airport, "LHR", "ourairports download failed: HTTP 503"));

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(DefaultCmd, CancellationToken.None));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("ourairports download failed: HTTP 503", ex.DiagnosticDetail);
    }

    [Fact]
    public async Task AtTheCap_WithAMissingReference_Is409AndProvisionsNothing()
    {
        // A refused track must write nothing at all — including reference rows.
        _refData.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _flights.CountActiveTrackedFlightsAsync(
                DefaultCmd.TravelerId, Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(20);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(DefaultCmd, CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        await _provisioner.DidNotReceive().ProvisionAsync(
            Arg.Any<IReadOnlyCollection<MissingReference>>(), Arg.Any<CancellationToken>());
    }
```

> The cap value in the last test must match the `SyncOptions` test double's `MaxActiveTrackedFlightsPerTraveler` in that file — read it and use that number rather than assuming 20.

- [ ] **Step 2: Run the tests to verify they fail**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~TrackFlightCommandHandlerTests"
```

Expected: FAIL to compile — the handler takes no `IReferenceDataProvisioner`.

- [ ] **Step 3: Take the dependency**

In `TrackFlightCommand.cs`, add the field, constructor parameter and assignment alongside the existing five dependencies:

```csharp
        private readonly IReferenceDataProvisioner _provisioner;
```

- [ ] **Step 4: Resolve, provision, re-resolve**

Replace the opening of `Handle` — from the `var airlineIata = ...` line down to and including the three `?? throw new DomainException(...)` lookups — with:

```csharp
            // An IATA flight designator's first two characters are the marketing airline's IATA
            // code (e.g. "6E4120" -> "6E") — there is no separate AirlineIata field to trust or
            // to contradict it, so this is now the single source of truth for which airline is
            // being tracked.
            var airlineIata = cmd.FlightNumber.Length >= 2 ? cmd.FlightNumber[..2] : cmd.FlightNumber;

            var airlineId = await _refData.GetAirlineIdByIataAsync(airlineIata, ct);
            var depAirportId = await _refData.GetAirportIdByIataAsync(cmd.DepartureAirportIata, ct);
            var arrAirportId = await _refData.GetAirportIdByIataAsync(cmd.ArrivalAirportIata, ct);

            var now = _timeProvider.GetUtcNow().UtcDateTime;

            if (airlineId is null || depAirportId is null || arrAirportId is null)
            {
                // The cap is checked before provisioning so a refused track still writes nothing
                // (#465). Only the count matters here: a missing reference row means no Flight row
                // can reference it, so this traveler cannot already be tracking this flight.
                var capBeforeProvisioning = _syncOptions.MaxActiveTrackedFlightsPerTraveler;
                if (await _flights.CountActiveTrackedFlightsAsync(
                        cmd.TravelerId, now, _syncOptions.StopAfterArrival, _syncOptions.StopWithoutArrivalAfter, ct)
                    >= capBeforeProvisioning)
                {
                    throw CapExceeded(capBeforeProvisioning);
                }

                var missing = new List<MissingReference>(3);
                if (airlineId is null)
                    missing.Add(new MissingReference(VerificationEntityType.Airline, airlineIata));
                if (depAirportId is null)
                    missing.Add(new MissingReference(VerificationEntityType.Airport, cmd.DepartureAirportIata));
                if (arrAirportId is null)
                    missing.Add(new MissingReference(VerificationEntityType.Airport, cmd.ArrivalAirportIata));

                var provision = await _provisioner.ProvisionAsync(missing, ct);
                if (provision.Status != ReferenceProvisionStatus.Provisioned)
                    throw ToDomainException(provision);

                // Re-resolve through our own DbContext: the provisioner wrote in a scope of its own.
                airlineId = await _refData.GetAirlineIdByIataAsync(airlineIata, ct)
                    ?? throw ReferenceNotFound(VerificationEntityType.Airline, airlineIata);
                depAirportId = await _refData.GetAirportIdByIataAsync(cmd.DepartureAirportIata, ct)
                    ?? throw ReferenceNotFound(VerificationEntityType.Airport, cmd.DepartureAirportIata);
                arrAirportId = await _refData.GetAirportIdByIataAsync(cmd.ArrivalAirportIata, ct)
                    ?? throw ReferenceNotFound(VerificationEntityType.Airport, cmd.ArrivalAirportIata);
            }
```

Delete the now-duplicated `var now = _timeProvider.GetUtcNow().UtcDateTime;` further down, and change the existing cap check to throw through the shared helper:

```csharp
            {
                throw CapExceeded(cap);
            }
```

Every later use of `airlineId`, `depAirportId` and `arrAirportId` is now `int?`; add `.Value` where `Flight.Create` and `AlreadyTracksAsync` need an `int`.

- [ ] **Step 5: Add the three helpers**

At the bottom of `TrackFlightCommandHandler`, beside `AlreadyTracksAsync`:

```csharp
        /// <summary>The one copy of the tracking-cap refusal, thrown from both the pre-provisioning
        /// check and the main one, so the two can never disagree about the wording.</summary>
        private static DomainException CapExceeded(int cap) =>
            new($"You can track up to {cap} flights at a time. Stop tracking one to add another.", 409);

        /// <summary>
        /// Turns a failed provisioning attempt into the right status. Only <c>NotFound</c> becomes a
        /// 404 -- a source answered and has no such entity. Everything else is a 503: we did not
        /// learn that the entity is absent, only that we could not find out.
        /// </summary>
        private static DomainException ToDomainException(ReferenceProvisionResult result) =>
            result.Status == ReferenceProvisionStatus.NotFound
                ? ReferenceNotFound(result.FailedType!.Value, result.FailedCode!)
                : new DomainException(
                    "Flight reference data could not be looked up right now. Please try again shortly.",
                    503,
                    result.DiagnosticDetail);

        /// <summary>The 404 messages are unchanged from before #465, so a client matching on them
        /// keeps working.</summary>
        private static DomainException ReferenceNotFound(VerificationEntityType type, string code) =>
            type switch
            {
                VerificationEntityType.Airline => new DomainException($"Airline '{code}' not found.", 404),
                VerificationEntityType.Airport => new DomainException($"Airport '{code}' not found.", 404),
                _ => new DomainException($"Country '{code}' not found.", 404)
            };
```

- [ ] **Step 6: Run the tests to verify they pass**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj --filter "FullyQualifiedName~TrackFlightCommandHandlerTests"
```

Expected: PASS, every pre-existing test in the class included.

- [ ] **Step 7: Run the whole Application and API suites**

```
dotnet test FlightAssistant.Application.Tests/FlightAssistant.Application.Tests.csproj
```

```
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj
```

Expected: PASS. The API suite exercises the real DI graph, so a missing registration fails here.

- [ ] **Step 8: Commit**

Stage `TrackFlightCommand.cs` and the test file, then commit with message:

```
feat(flights): provision missing reference data instead of 404ing on track (#465)
```

---

## Task 11: Document the route and regenerate the spec

**Files:**
- Modify: `FlightAssistant.API/Endpoints/FlightEndpoints.cs:104-140`
- Modify: `spec/openapi.json` (generated — do not hand-edit)

**Interfaces:**
- Consumes: the behaviour from Task 10.
- Produces: nothing code-facing.

**Why:** `.Produces*` lists every status **only this operation can produce**. 503 is now one of them, and it is route-specific rather than ambient, so it must be declared. 429 stays undeclared by this repository's separate, deliberate convention.

- [ ] **Step 1: Declare the 503 and describe it**

On the `POST /flights` route, add to the existing `.Produces*` chain (which already has `.Produces(201)`, `.ProducesValidationProblem()`, `.ProducesProblem(404)` and `.ProducesProblem(409)`):

```csharp
            .ProducesProblem(503)
```

Replace the final line of that route's `.WithDescription(...)` — `"Returns 404 if the airline or either airport in the request is not known to the system. "` — with:

```csharp
                "If the airline or either airport is not already known to the system, it is looked up in the external " +
                "reference sources and created automatically, so a row returned by GET /flights/search can be tracked " +
                "even for an airport or airline nobody has imported yet. " +
                "Returns 404 only when a source answered and genuinely has no such airline, airport or country. " +
                "Returns 503 with a Retry-After header when the lookup could not be completed — a source was " +
                "unreachable, the flight data budget is spent, or the lookup took longer than the configured budget; " +
                "the dataset download continues in the background, so a retry shortly afterwards usually succeeds.")
```

- [ ] **Step 2: Regenerate the OpenAPI spec**

From the repository root of this worktree:

```
scripts/generate-openapi.sh
```

- [ ] **Step 3: Verify the regenerated spec changed only as expected**

```
git diff --stat spec/openapi.json
```

Expected: only the `POST /flights` operation's `description` and its `responses` gaining `"503"`. If anything else moved, stop and investigate — CI regenerates this file on every PR and fails on a byte difference.

- [ ] **Step 4: Run the API suite**

```
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj
```

Expected: PASS.

- [ ] **Step 5: Commit**

Stage `FlightEndpoints.cs` and `spec/openapi.json`, then commit with message:

```
docs(api): declare the provisioning 503 on POST /flights (#465)
```

---

## Task 12: Prove the foreign-key ordering and the audit rows against PostgreSQL

**Files:**
- Create: `FlightAssistant.Infrastructure.Tests/Persistance/ReferenceProvisioningPostgresTests.cs`

**Interfaces:**
- Consumes: `ReferenceDataProvisioner` and everything it needs.
- Produces: nothing code-facing.

**Why:** the API and Application suites cannot see either guarantee. The InMemory provider enforces no foreign key, so a country inserted *after* its airport would pass there and fail in production; and `FlightAssistantWebApplicationFactory` strips `AdminAuditInterceptor` entirely (#319), so no API-level test can prove an audit row was written. This is exactly the class of divergence the #295 Testcontainers harness exists for. **Requires Docker.**

Follow the fixture and collection attributes used by `AdminAuditInterceptorPostgresTests` in the same folder — use that file's existing pattern for obtaining a real `FlightAssistantDbContext` with the interceptor attached, rather than inventing a new harness.

- [ ] **Step 1: Write the failing tests**

Create `FlightAssistant.Infrastructure.Tests/Persistance/ReferenceProvisioningPostgresTests.cs` with these three tests. Build a `ServiceCollection` registering the real `AdminContext`/`IAdminContext`, the real `AirportRepository`, `AirlineRepository` and `CountryRepository` over the fixture's `DbContext`, then construct `ReferenceDataProvisioner` with fake sources exactly as `ReferenceDataProvisionerTests` does.

```csharp
    [Fact]
    public async Task ProvisioningAnAirportInAnUnknownCountry_SatisfiesTheForeignKey()
    {
        // The country row must be INSERTed before the airport that references it. InMemory enforces
        // no foreign key, so only a real PostgreSQL run can prove this ordering holds.
        var result = await provisioner.ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        Assert.NotNull(await db.Countries.SingleOrDefaultAsync(c => c.Code == "GB"));
        var airport = await db.Airports.SingleAsync(a => a.Iata == "LHR");
        Assert.Equal("GB", airport.CountryCode);
        Assert.True(airport.IsVerified);
        Assert.Equal("auto", airport.SourceType);
    }

    [Fact]
    public async Task ProvisionedRows_AreAuditedUnderTheSystemActor()
    {
        await provisioner.ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        var rows = await db.AdminAuditLogs
            .Where(a => a.EntityType == nameof(Airport) || a.EntityType == nameof(Country))
            .ToListAsync();

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(VerificationJobActor.ActorId, r.ActorId));
    }

    [Fact]
    public async Task AFailedProvision_LeavesNoPartialRows()
    {
        // The airport resolves but its country does not, so nothing may be committed: an airport
        // row without its country would violate the foreign key anyway.
        var result = await provisionerWhoseCountrySourceHasNothing.ProvisionAsync(
            [new MissingReference(VerificationEntityType.Airport, "LHR")], CancellationToken.None);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Empty(await db.Airports.Where(a => a.Iata == "LHR").ToListAsync());
        Assert.Empty(await db.Countries.Where(c => c.Code == "GB").ToListAsync());
    }
```

> `db.AdminAuditLogs`, `db.Airports` and `db.Countries` must match the real `DbSet` names on `FlightAssistantDbContext`; read them off the context rather than assuming. Likewise the `AdminAuditLog` property holding the actor — use the name the entity actually declares.

- [ ] **Step 2: Run them to verify they fail**

```
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ReferenceProvisioningPostgresTests"
```

Expected: FAIL to compile, then fail on assertions until the wiring is right. If any test fails because the INSERT order violates the foreign key, that is a genuine defect in Task 5 — fix the provisioner, not the test.

- [ ] **Step 3: Make them pass**

No production change should be needed: Tasks 4–9 already implement this. If the foreign key fails, add explicit ordering by saving countries before the airports and airlines rather than relying on EF's own graph ordering, and record in the code comment that the ordering is explicit because it was measured to be necessary.

- [ ] **Step 4: Run the whole Infrastructure suite**

```
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj
```

Expected: PASS (744 pre-existing tests plus the new ones).

- [ ] **Step 5: Commit**

Stage the new test file (plus the provisioner if Step 3 required a change), then commit with message:

```
test(reference-data): prove FK ordering and system-actor auditing on PostgreSQL (#465)
```

---

## Task 13: Document it in CLAUDE.md

**Files:**
- Modify: `CLAUDE.md`

**Why:** CLAUDE.md is where this repository records decisions a future reader would otherwise reverse. Three here are easy to undo by accident: that a 404 now means something narrower than before, that the provisioner writes in its own scope for an auditing reason, and that the extra non-checked-column key is deliberate.

- [ ] **Step 1: Add the behaviour section**

In the **Key Patterns** section, after the flight-tracking trigger paragraphs, add:

```markdown
**A missing airline, airport or country is fetched rather than refused (#465).** `POST /flights` (and the admin track route, which sends the same command) used to answer 404 whenever `TrackFlightCommandHandler`'s three id lookups missed — but `GET /flights/search` never consults our database, so the picker routinely offered flights that could not be tracked. `ReferenceDataProvisioner` (Application, `ReferenceData/`) now looks the entity up through `IVerificationSourceRegistry`, using the source named in `Verification:DefaultSources:*`, creates the row, stamps it `Verify(VerificationJobActor.ActorId, sourceName)` with `SourceType = "auto"`, and the track proceeds. **404 now means a source answered and genuinely has no such entity**; everything else — a source error, an unregistered source, the budget, the deadline — is a 503 with `Retry-After`, because we did not learn the entity is absent, only that we could not find out. Countries are resolved *after* the airport or airline that needs them (the country is not known until the source answers) and written *before* them, since `CountryCode` is a foreign key. An airline needs one extra step: `aeroapi` reports an operator's country as a name, never a code (which is why #401 R1 removed `CountryCode` from the airline checked-column list), so `AeroApiVerificationSource` also emits it under `VerificationExtraValues.AirlineCountryName` — a key `VerificationComparer` ignores, since it reads only registered checked columns — and `ICountryNameResolver` (implemented by `MledozeCountriesVerificationSource`, over a reverse index built in the same parse pass) turns it into a code. A name that will not resolve is a 404 on the airline: no valid row can be built without the foreign key. **Provisioning is all-or-nothing** — one save, one transaction, nothing written on failure. Keeping partial success was considered and rejected: the only metered source is `aeroapi` (airlines), and in the scenario partial success would help, the airline is precisely what failed, so there is nothing metered to salvage; airports and countries come from free sources held in memory for 24 hours, so a retry re-fetches them for effectively nothing. The cost would have been a second write path deciding which rows are still safe to save when one of their countries did not resolve, and a reference table a failed request can leave half-populated.

**The provisioner writes in its own DI scope, and that is not incidental.** It saves through `VerificationJobActor.CreateScope`, never the traveler's request scope, because `AdminAuditInterceptor` audits every tracked change in a `SaveChanges` that carries an actor — so setting one on the traveler's own scope would begin auditing ordinary traveler flight writes, which CLAUDE.md elsewhere says must stay untouched. The separate scope audits the reference writes under `system` and leaves the traveler's write alone. It passes no correlation id: the request's id lives in `HttpContext.Items`, which Application cannot see, so these rows fall back to `Activity.Current?.Id` like any other actor-less path — a known gap, not an oversight. The tracking cap is checked **before** provisioning so a refused track still writes nothing; that is sound because a missing reference row means no `Flight` row can reference it, so the traveler cannot already be tracking that flight and only the count matters. Two guards sit in front of metered calls only: a negative cache (`refdata:missing:{type}:{code}`, `ReferenceProvisioning:NegativeCacheTtl`) so a repeated bogus designator is paid for once — `POST /flights` accepts any well-formed number, so a caller could otherwise drain the AeroAPI budget at 10 requests a minute — and `IProviderBudgetGuard`, refusing from `SoftLimit` onward to match `AeroApiFlightSearchBudgetDecorator` rather than #401's `Exhausted`-only rule, which is for work an admin explicitly asked for. Only a miss is cached, never a hit and never a failure, which would turn a brief outage into hours of 404s. **No background queue is needed on a timeout**: `VerificationDatasetCache` shares one download across callers and a caller's own cancellation cancels only its own *wait*, so the download keeps running and a retry finds it warm. `ReferenceProvisioningPostgresTests` proves the foreign-key ordering and the `system` audit rows against real PostgreSQL — the InMemory provider enforces no foreign key, and `FlightAssistantWebApplicationFactory` strips the audit interceptor entirely (#319), so neither is visible to the API suite.
```

- [ ] **Step 2: Add the configuration keys**

In the **Required Configuration** table, after the `Verification:*` rows:

```markdown
| `ReferenceProvisioning:Enabled` | Optional; default `true` (#465). Gates auto-provisioning of a missing airline/airport/country on track. `false` restores the pre-#465 404 exactly |
| `ReferenceProvisioning:Budget` | Optional; default `00:00:10`, range 1 s–60 s. One wall-clock budget for the whole provisioning step; exceeding it is a 503, never a 404 |
| `ReferenceProvisioning:NegativeCacheTtl` | Optional; default `24:00:00`, range 0–7 days. How long "no such entity" is remembered, so a repeated bogus designator costs nothing. `0` disables the cache |
```

- [ ] **Step 3: Note the search/track relationship**

In the paragraph describing `GET /flights/search`, add:

```markdown
Search still never consults our database, but since #465 a row it returns can be tracked even when the airline or an airport is not in our reference data — `POST /flights` provisions it.
```

- [ ] **Step 4: Run the full suite**

```
dotnet build --configuration Release
```

```
dotnet test
```

Expected: every suite passes.

- [ ] **Step 5: Commit**

Stage `CLAUDE.md`, then commit with message:

```
docs: record reference-data auto-provisioning (#465)
```

---

## Done

Open a PR into `development` titled **"Auto-provision missing reference data instead of 404ing on track (#465)"**, describing: the search/track mismatch it closes, the narrowed meaning of 404, the new 503, the two cost guards, and the `ReferenceProvisioning` configuration keys.
