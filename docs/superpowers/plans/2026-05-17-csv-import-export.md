# CSV Import/Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the JSON-based import/export for admin reference data with CSV files for better human readability and editability.

**Architecture:** CSV parsing and serialization happens entirely at the API layer — the Application layer (commands, queries, DTOs, handlers) is unchanged. `CsvHelper` provides the CSV engine. Endpoints switch from JSON body to `IFormFile` for imports and from JSON bytes to `text/csv` bytes for exports. Nested collections (`Providers` on Lounges, `Tiers` on LoyaltyPrograms) are encoded as a JSON string in a single CSV column — flat fields remain plain CSV.

**Tech Stack:** CsvHelper 33.x, System.Text.Json (for nested column encoding), ASP.NET Core minimal APIs, xUnit + NSubstitute for new API.Tests project.

---

## File Map

| Action | File |
|--------|------|
| Create | `FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj` |
| Create | `FlightAssistant.API.Tests/Csv/CsvImportParserTests.cs` |
| Create | `FlightAssistant.API.Tests/Csv/CsvExportWriterTests.cs` |
| Modify | `FlightAssistant.API/FlightAssistant.API.csproj` |
| Create | `FlightAssistant.API/Csv/CsvImportParser.cs` |
| Create | `FlightAssistant.API/Csv/CsvExportWriter.cs` |
| Create | `FlightAssistant.API/Csv/ImportMaps.cs` |
| Create | `FlightAssistant.API/Csv/ExportMaps.cs` |
| Modify | `FlightAssistant.API/Endpoints/Admin/AdminImportEndpoints.cs` |
| Modify | `FlightAssistant.API/Endpoints/Admin/AdminExportEndpoints.cs` |

---

## Task 1: Add packages and create FlightAssistant.API.Tests project

**Files:**
- Modify: `FlightAssistant.API/FlightAssistant.API.csproj`
- Create: `FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj`

- [ ] **Step 1: Add CsvHelper to the API project**

```bash
dotnet add FlightAssistant.API/FlightAssistant.API.csproj package CsvHelper
```

Expected: Package added. Verify `<PackageReference Include="CsvHelper"` appears in the .csproj.

- [ ] **Step 2: Create the API test project**

```bash
dotnet new xunit -n FlightAssistant.API.Tests --framework net8.0
dotnet sln FlightAssistantAPI.sln add FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj
dotnet add FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj reference FlightAssistant.API/FlightAssistant.API.csproj
dotnet add FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj package NSubstitute
dotnet add FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj package coverlet.collector
```

- [ ] **Step 3: Remove the boilerplate test that dotnet new generates**

Delete `FlightAssistant.API.Tests/UnitTest1.cs`.

- [ ] **Step 4: Verify the project builds**

```bash
dotnet build FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add FlightAssistant.API/FlightAssistant.API.csproj FlightAssistant.API.Tests/ FlightAssistantAPI.sln
git commit -m "chore: add CsvHelper package and create FlightAssistant.API.Tests project"
```

---

## Task 2: Create CsvImportParser, ImportMaps, and tests

**Files:**
- Create: `FlightAssistant.API/Csv/CsvImportParser.cs`
- Create: `FlightAssistant.API/Csv/ImportMaps.cs`
- Create: `FlightAssistant.API.Tests/Csv/CsvImportParserTests.cs`

- [ ] **Step 1: Write the failing tests first**

Create `FlightAssistant.API.Tests/Csv/CsvImportParserTests.cs`:

```csharp
using System.Text;
using FlightAssistant.API.Csv;
using FlightAssistant.Application.Commands.Admin.Import;

namespace FlightAssistant.API.Tests.Csv;

public class CsvImportParserTests
{
    // ── Flat entity ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParseAlliances_ValidCsv_ReturnsDtos()
    {
        const string csv = """
            Code,Name
            SA,Star Alliance
            OW,Oneworld
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<AllianceImportDto, AllianceImportDtoMap>(stream);

        Assert.Equal(2, result.Count);
        Assert.Equal("SA", result[0].Code);
        Assert.Equal("Star Alliance", result[0].Name);
        Assert.Equal("OW", result[1].Code);
    }

    [Fact]
    public async Task ParseAirports_WithNullableFields_ReturnsDtos()
    {
        const string csv = """
            Iata,Icao,Name,City,CountryCode,Latitude,Longitude,ElevationFt,Timezone
            LHR,EGLL,Heathrow,London,GB,51.477,-0.461,83,Europe/London
            SVO,,Sheremetyevo,Moscow,RU,,,,,
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<AirportImportDto, AirportImportDtoMap>(stream);

        Assert.Equal(2, result.Count);
        Assert.Equal("LHR", result[0].Iata);
        Assert.Equal(51.477, result[0].Latitude);
        Assert.Equal(83, result[0].ElevationFt);
        Assert.Equal("SVO", result[1].Iata);
        Assert.Null(result[1].Icao);
        Assert.Null(result[1].Latitude);
        Assert.Null(result[1].ElevationFt);
    }

    // ── Nested entity (JSON column) ────────────────────────────────────────────

    [Fact]
    public async Task ParseLounges_WithProvidersJsonColumn_ReturnsDtos()
    {
        const string csv = """
            Name,AirportIata,Terminal,LocationHint,Hours,IsAirside,IsDomesticOnly,IsInternationalOnly,Amenities,Providers
            "The Lounge",LHR,T5,,24h,true,false,false,,"[{""providerCode"":""AMEX"",""role"":""Primary"",""minAllianceTierCode"":null,""notes"":null}]"
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<LoungeImportDto, LoungeImportDtoMap>(stream);

        Assert.Single(result);
        Assert.Equal("The Lounge", result[0].Name);
        Assert.Single(result[0].Providers);
        Assert.Equal("AMEX", result[0].Providers[0].ProviderCode);
        Assert.Equal("Primary", result[0].Providers[0].Role);
    }

    [Fact]
    public async Task ParseLounges_EmptyProviders_ReturnsEmptyList()
    {
        const string csv = """
            Name,AirportIata,Terminal,LocationHint,Hours,IsAirside,IsDomesticOnly,IsInternationalOnly,Amenities,Providers
            "Empty Lounge",JFK,T4,,,,false,false,,[]
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await CsvImportParser.ParseAsync<LoungeImportDto, LoungeImportDtoMap>(stream);

        Assert.Single(result);
        Assert.Empty(result[0].Providers);
    }
}
```

- [ ] **Step 2: Run to verify tests fail**

```bash
dotnet test FlightAssistant.API.Tests/ --filter "ClassName=CsvImportParserTests" 2>&1 | tail -5
```

Expected: Build error — `CsvImportParser` and map types not found yet.

- [ ] **Step 3: Create the JSON type converter for nested collections**

Create `FlightAssistant.API/Csv/CsvImportParser.cs`:

```csharp
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
using System.Globalization;
using System.Text.Json;

namespace FlightAssistant.API.Csv;

public static class CsvImportParser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<IReadOnlyList<T>> ParseAsync<T, TMap>(Stream stream)
        where TMap : ClassMap<T>, new()
    {
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Context.RegisterClassMap<TMap>();
        var records = await csv.GetRecordsAsync<T>().ToListAsync();
        return records;
    }

    // Used by ClassMaps for nested JSON columns.
    public static readonly JsonListConverter JsonList = new();

    public sealed class JsonListConverter : DefaultTypeConverter
    {
        public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
        {
            if (string.IsNullOrWhiteSpace(text) || text == "[]")
                return Array.Empty<object>();

            var targetType = memberMapData.Member?.MemberType()
                ?? throw new InvalidOperationException("Cannot determine target type for JSON column.");
            return JsonSerializer.Deserialize(text, targetType, JsonOpts);
        }

        public override string? ConvertToString(object? value, IWriterRow row, MemberMapData memberMapData)
            => value is null ? "[]" : JsonSerializer.Serialize(value, JsonOpts);
    }
}
```

> **Note:** CsvHelper's `GetRecordsAsync` returns `IAsyncEnumerable<T>`. The `.ToListAsync()` call requires `using System.Linq;` or `Microsoft.Extensions.Linq`. If not available, use `var records = new List<T>(); await foreach (var r in csv.GetRecordsAsync<T>()) records.Add(r);` instead.

- [ ] **Step 4: Create ImportMaps.cs with ClassMaps for all 8 entities**

Create `FlightAssistant.API/Csv/ImportMaps.cs`:

```csharp
using CsvHelper.Configuration;
using FlightAssistant.Application.Commands.Admin.Import;

namespace FlightAssistant.API.Csv;

// ── Flat entities ──────────────────────────────────────────────────────────────

public sealed class AllianceImportDtoMap : ClassMap<AllianceImportDto>
{
    public AllianceImportDtoMap() { AutoMap(System.Globalization.CultureInfo.InvariantCulture); }
}

public sealed class CountryImportDtoMap : ClassMap<CountryImportDto>
{
    public CountryImportDtoMap() { AutoMap(System.Globalization.CultureInfo.InvariantCulture); }
}

public sealed class AirportImportDtoMap : ClassMap<AirportImportDto>
{
    public AirportImportDtoMap() { AutoMap(System.Globalization.CultureInfo.InvariantCulture); }
}

public sealed class AirlineImportDtoMap : ClassMap<AirlineImportDto>
{
    public AirlineImportDtoMap() { AutoMap(System.Globalization.CultureInfo.InvariantCulture); }
}

public sealed class LoungeProviderImportDtoMap : ClassMap<LoungeProviderImportDto>
{
    public LoungeProviderImportDtoMap() { AutoMap(System.Globalization.CultureInfo.InvariantCulture); }
}

public sealed class LoyaltyProgramKindImportDtoMap : ClassMap<LoyaltyProgramKindImportDto>
{
    public LoyaltyProgramKindImportDtoMap() { AutoMap(System.Globalization.CultureInfo.InvariantCulture); }
}

// ── Nested entities (JSON columns for collections) ─────────────────────────────

public sealed class LoungeImportDtoMap : ClassMap<LoungeImportDto>
{
    public LoungeImportDtoMap()
    {
        Map(m => m.Name);
        Map(m => m.AirportIata);
        Map(m => m.Terminal);
        Map(m => m.LocationHint);
        Map(m => m.Hours);
        Map(m => m.IsAirside);
        Map(m => m.IsDomesticOnly);
        Map(m => m.IsInternationalOnly);
        Map(m => m.Amenities);
        Map(m => m.Providers).TypeConverter(CsvImportParser.JsonList);
    }
}

public sealed class LoyaltyProgramImportDtoMap : ClassMap<LoyaltyProgramImportDto>
{
    public LoyaltyProgramImportDtoMap()
    {
        Map(m => m.Code);
        Map(m => m.Name);
        Map(m => m.KindCode);
        Map(m => m.AirlineIata);
        Map(m => m.AllianceCode);
        Map(m => m.Tiers).TypeConverter(CsvImportParser.JsonList);
    }
}
```

> **Note:** `AutoMap` works for flat records. If CsvHelper can't auto-discover properties on `record` types (rare), use explicit `Map(m => m.Prop)` for each field as shown in `LoungeImportDtoMap`.

- [ ] **Step 5: Run tests**

```bash
dotnet test FlightAssistant.API.Tests/ --filter "ClassName=CsvImportParserTests" -v normal
```

Expected: All 4 tests pass.

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.API/Csv/ FlightAssistant.API.Tests/Csv/CsvImportParserTests.cs
git commit -m "feat(csv): add CsvImportParser with ClassMaps for all 8 import DTOs"
```

---

## Task 3: Create CsvExportWriter, ExportMaps, and tests

**Files:**
- Create: `FlightAssistant.API/Csv/CsvExportWriter.cs`
- Create: `FlightAssistant.API/Csv/ExportMaps.cs`
- Create: `FlightAssistant.API.Tests/Csv/CsvExportWriterTests.cs`

- [ ] **Step 1: Write failing tests**

Create `FlightAssistant.API.Tests/Csv/CsvExportWriterTests.cs`:

```csharp
using System.Text;
using FlightAssistant.API.Csv;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Queries.Admin.Export;

namespace FlightAssistant.API.Tests.Csv;

public class CsvExportWriterTests
{
    [Fact]
    public void WriteAlliances_ReturnsCsvWithHeaderAndRows()
    {
        var data = new List<AllianceExportDto>
        {
            new("SA", "Star Alliance"),
            new("OW", "Oneworld")
        };

        var bytes = CsvExportWriter.Write<AllianceExportDto, AllianceExportDtoMap>(data);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("Code,Name", text);
        Assert.Contains("SA,Star Alliance", text);
        Assert.Contains("OW,Oneworld", text);
    }

    [Fact]
    public void WriteAirports_WithNullableFields_WritesEmptyForNull()
    {
        var data = new List<AirportExportDto>
        {
            new("SVO", null, "Sheremetyevo", "Moscow", "RU", null, null, null, null)
        };

        var bytes = CsvExportWriter.Write<AirportExportDto, AirportExportDtoMap>(data);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("SVO,", text);
        // null Icao should produce empty field, not the word "null"
        Assert.DoesNotContain("null", text);
    }

    [Fact]
    public void WriteLounges_WithProviders_EncodesProvidersAsJson()
    {
        var data = new List<LoungeExportDto>
        {
            new("The Lounge", "LHR", "T5", null, "24h", true, false, false, null,
                [new LoungeProviderLinkExportDto("AMEX", "Primary", null, null)])
        };

        var bytes = CsvExportWriter.Write<LoungeExportDto, LoungeExportDtoMap>(data);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("Providers", text); // header
        Assert.Contains("AMEX", text);
        Assert.Contains("Primary", text);
    }

    [Fact]
    public void WriteLoyaltyPrograms_WithTiers_EncodesTiersAsJson()
    {
        var data = new List<LoyaltyProgramExportDto>
        {
            new("BAEC", "Executive Club", "AIRLINE", "BA", null,
                [new LoyaltyTierExportDto("GOLD", "Gold", 1, null, null)])
        };

        var bytes = CsvExportWriter.Write<LoyaltyProgramExportDto, LoyaltyProgramExportDtoMap>(data);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("BAEC", text);
        Assert.Contains("GOLD", text);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test FlightAssistant.API.Tests/ --filter "ClassName=CsvExportWriterTests" 2>&1 | tail -5
```

Expected: Build error — types not defined yet.

- [ ] **Step 3: Create CsvExportWriter.cs**

Create `FlightAssistant.API/Csv/CsvExportWriter.cs`:

```csharp
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FlightAssistant.API.Csv;

public static class CsvExportWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static byte[] Write<T, TMap>(IReadOnlyList<T> data)
        where TMap : ClassMap<T>, new()
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.Context.RegisterClassMap<TMap>();
        csv.WriteRecords(data);
        writer.Flush();
        return ms.ToArray();
    }

    // Reused by ExportMaps for nested JSON columns.
    public static readonly JsonCollectionConverter JsonCollection = new();

    public sealed class JsonCollectionConverter : DefaultTypeConverter
    {
        public override string? ConvertToString(object? value, IWriterRow row, MemberMapData memberMapData)
            => value is null ? "[]" : JsonSerializer.Serialize(value, JsonOpts);

        public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
            => text; // export only; reading not needed
    }
}
```

- [ ] **Step 4: Create ExportMaps.cs**

Create `FlightAssistant.API/Csv/ExportMaps.cs`:

```csharp
using CsvHelper.Configuration;
using FlightAssistant.Application.Queries.Admin.Export;

namespace FlightAssistant.API.Csv;

// ── Flat entities ──────────────────────────────────────────────────────────────

public sealed class AllianceExportDtoMap : ClassMap<AllianceExportDto>
{
    public AllianceExportDtoMap() { AutoMap(System.Globalization.CultureInfo.InvariantCulture); }
}

public sealed class CountryExportDtoMap : ClassMap<CountryExportDto>
{
    public CountryExportDtoMap() { AutoMap(System.Globalization.CultureInfo.InvariantCulture); }
}

public sealed class AirportExportDtoMap : ClassMap<AirportExportDto>
{
    public AirportExportDtoMap() { AutoMap(System.Globalization.CultureInfo.InvariantCulture); }
}

public sealed class AirlineExportDtoMap : ClassMap<AirlineExportDto>
{
    public AirlineExportDtoMap() { AutoMap(System.Globalization.CultureInfo.InvariantCulture); }
}

public sealed class LoungeProviderExportDtoMap : ClassMap<LoungeProviderExportDto>
{
    public LoungeProviderExportDtoMap() { AutoMap(System.Globalization.CultureInfo.InvariantCulture); }
}

public sealed class LoyaltyProgramKindExportDtoMap : ClassMap<LoyaltyProgramKindExportDto>
{
    public LoyaltyProgramKindExportDtoMap() { AutoMap(System.Globalization.CultureInfo.InvariantCulture); }
}

// ── Nested entities ────────────────────────────────────────────────────────────

public sealed class LoungeExportDtoMap : ClassMap<LoungeExportDto>
{
    public LoungeExportDtoMap()
    {
        Map(m => m.Name);
        Map(m => m.AirportIata);
        Map(m => m.Terminal);
        Map(m => m.LocationHint);
        Map(m => m.Hours);
        Map(m => m.IsAirside);
        Map(m => m.IsDomesticOnly);
        Map(m => m.IsInternationalOnly);
        Map(m => m.Amenities);
        Map(m => m.Providers).TypeConverter(CsvExportWriter.JsonCollection);
    }
}

public sealed class LoyaltyProgramExportDtoMap : ClassMap<LoyaltyProgramExportDto>
{
    public LoyaltyProgramExportDtoMap()
    {
        Map(m => m.Code);
        Map(m => m.Name);
        Map(m => m.KindCode);
        Map(m => m.AirlineIata);
        Map(m => m.AllianceCode);
        Map(m => m.Tiers).TypeConverter(CsvExportWriter.JsonCollection);
    }
}
```

- [ ] **Step 5: Run tests**

```bash
dotnet test FlightAssistant.API.Tests/ --filter "ClassName=CsvExportWriterTests" -v normal
```

Expected: All 4 tests pass.

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.API/Csv/ FlightAssistant.API.Tests/Csv/CsvExportWriterTests.cs
git commit -m "feat(csv): add CsvExportWriter with ClassMaps for all 8 export DTOs"
```

---

## Task 4: Update AdminImportEndpoints.cs to accept CSV files

**Files:**
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminImportEndpoints.cs`

- [ ] **Step 1: Replace AdminImportEndpoints.cs**

The file currently binds `IReadOnlyList<XxxImportDto>` directly from the JSON body. Replace all 8 endpoint lambdas to accept `IFormFile file` and parse via `CsvImportParser`. Full replacement:

```csharp
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.API.Csv;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin;

public static class AdminImportEndpoints
{
    public static void MapAdminImportEndpoints(this IEndpointRouteBuilder admin)
    {
        var group = admin.MapGroup("import").WithTags("Admin - Import")
            .DisableAntiforgery();

        group.MapPost("alliances", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<AllianceImportDto, AllianceImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportAlliancesCommand(items), ct));
        }).WithName("ImportAlliances")
          .WithSummary("Import alliances from CSV (run before countries, airports, airlines). Upserts by Code.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("countries", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<CountryImportDto, CountryImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportCountriesCommand(items), ct));
        }).WithName("ImportCountries")
          .WithSummary("Import countries from CSV (run after alliances). Upserts by Code.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("airports", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<AirportImportDto, AirportImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportAirportsCommand(items), ct));
        }).WithName("ImportAirports")
          .WithSummary("Import airports from CSV (run after countries). Upserts by IATA.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("airlines", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<AirlineImportDto, AirlineImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportAirlinesCommand(items), ct));
        }).WithName("ImportAirlines")
          .WithSummary("Import airlines from CSV (run after airports). Upserts by IATA, fallback ICAO.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("lounge-providers", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<LoungeProviderImportDto, LoungeProviderImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportLoungeProvidersCommand(items), ct));
        }).WithName("ImportLoungeProviders")
          .WithSummary("Import lounge providers from CSV (run after airlines). Upserts by Code.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("loyalty-program-kinds", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<LoyaltyProgramKindImportDto, LoyaltyProgramKindImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportLoyaltyProgramKindsCommand(items), ct));
        }).WithName("ImportLoyaltyProgramKinds")
          .WithSummary("Import loyalty program kinds from CSV (run after airlines). Upserts by Code.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("lounges", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<LoungeImportDto, LoungeImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportLoungesCommand(items), ct));
        }).WithName("ImportLounges")
          .WithSummary("Import lounges from CSV (run after lounge-providers). Providers column is JSON-encoded. Upserts by Name+AirportIata+Terminal.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("loyalty-programs", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<LoyaltyProgramImportDto, LoyaltyProgramImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportLoyaltyProgramsCommand(items), ct));
        }).WithName("ImportLoyaltyPrograms")
          .WithSummary("Import loyalty programs from CSV (run after loyalty-program-kinds and airlines). Tiers column is JSON-encoded. Upserts by Code.")
          .Accepts<IFormFile>("text/csv");
    }
}
```

- [ ] **Step 2: Build to confirm no errors**

```bash
dotnet build FlightAssistant.API/ --configuration Release 2>&1 | tail -10
```

Expected: Build succeeded, 0 errors. Fix any using directive issues if needed.

- [ ] **Step 3: Commit**

```bash
git add FlightAssistant.API/Endpoints/Admin/AdminImportEndpoints.cs
git commit -m "feat(csv): update import endpoints to accept CSV file uploads instead of JSON body"
```

---

## Task 5: Update AdminExportEndpoints.cs to return CSV

**Files:**
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminExportEndpoints.cs`

- [ ] **Step 1: Replace AdminExportEndpoints.cs**

Remove the `JsonSerializerOptions` field and replace all `JsonSerializer.SerializeToUtf8Bytes` calls with `CsvExportWriter.Write`:

```csharp
using FlightAssistant.Application.Queries.Admin.Export;
using FlightAssistant.API.Csv;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin;

public static class AdminExportEndpoints
{
    public static void MapAdminExportEndpoints(this IEndpointRouteBuilder admin)
    {
        var group = admin.MapGroup("export").WithTags("Admin - Export");

        group.MapGet("airports", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportAirportsQuery(), ct);
            return Results.File(CsvExportWriter.Write<AirportExportDto, AirportExportDtoMap>(data), "text/csv", "airports-export.csv");
        }).WithName("ExportAirports").WithSummary("Export all verified airports as CSV");

        group.MapGet("airlines", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportAirlinesQuery(), ct);
            return Results.File(CsvExportWriter.Write<AirlineExportDto, AirlineExportDtoMap>(data), "text/csv", "airlines-export.csv");
        }).WithName("ExportAirlines").WithSummary("Export all verified airlines as CSV");

        group.MapGet("alliances", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportAlliancesQuery(), ct);
            return Results.File(CsvExportWriter.Write<AllianceExportDto, AllianceExportDtoMap>(data), "text/csv", "alliances-export.csv");
        }).WithName("ExportAlliances").WithSummary("Export all verified alliances as CSV");

        group.MapGet("countries", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportCountriesQuery(), ct);
            return Results.File(CsvExportWriter.Write<CountryExportDto, CountryExportDtoMap>(data), "text/csv", "countries-export.csv");
        }).WithName("ExportCountries").WithSummary("Export all verified countries as CSV");

        group.MapGet("lounges", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportLoungesQuery(), ct);
            return Results.File(CsvExportWriter.Write<LoungeExportDto, LoungeExportDtoMap>(data), "text/csv", "lounges-export.csv");
        }).WithName("ExportLounges").WithSummary("Export all verified lounges as CSV (Providers column is JSON-encoded)");

        group.MapGet("lounge-providers", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportLoungeProvidersQuery(), ct);
            return Results.File(CsvExportWriter.Write<LoungeProviderExportDto, LoungeProviderExportDtoMap>(data), "text/csv", "lounge-providers-export.csv");
        }).WithName("ExportLoungeProviders").WithSummary("Export all verified lounge providers as CSV");

        group.MapGet("loyalty-programs", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportLoyaltyProgramsQuery(), ct);
            return Results.File(CsvExportWriter.Write<LoyaltyProgramExportDto, LoyaltyProgramExportDtoMap>(data), "text/csv", "loyalty-programs-export.csv");
        }).WithName("ExportLoyaltyPrograms").WithSummary("Export all verified loyalty programs as CSV (Tiers column is JSON-encoded)");

        group.MapGet("loyalty-program-kinds", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportLoyaltyProgramKindsQuery(), ct);
            return Results.File(CsvExportWriter.Write<LoyaltyProgramKindExportDto, LoyaltyProgramKindExportDtoMap>(data), "text/csv", "loyalty-program-kinds-export.csv");
        }).WithName("ExportLoyaltyProgramKinds").WithSummary("Export all verified loyalty program kinds as CSV");
    }
}
```

- [ ] **Step 2: Build the full solution**

```bash
dotnet build --configuration Release 2>&1 | tail -10
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run all tests**

```bash
dotnet test --configuration Release --no-build -v normal
```

Expected: All tests pass (the Application.Tests are unchanged; the new API.Tests CSV tests should pass).

- [ ] **Step 4: Commit**

```bash
git add FlightAssistant.API/Endpoints/Admin/AdminExportEndpoints.cs
git commit -m "feat(csv): update export endpoints to return CSV files instead of JSON"
```

---

## CSV Column Reference

Use this when creating or editing CSV files manually:

| Entity | CSV Columns |
|--------|-------------|
| Alliances | `Code,Name` |
| Countries | `Code,Name,Alpha3,NumericCode,Region,Subregion,IsEU,IsSchengen,IsEEA` |
| Airports | `Iata,Icao,Name,City,CountryCode,Latitude,Longitude,ElevationFt,Timezone` |
| Airlines | `Iata,Icao,Name,CountryCode,AllianceCode` |
| LoungeProviders | `Code,Name,Kind,Description` |
| LoyaltyProgramKinds | `Code,Name,Description` |
| Lounges | `Name,AirportIata,Terminal,LocationHint,Hours,IsAirside,IsDomesticOnly,IsInternationalOnly,Amenities,Providers` (Providers = JSON array) |
| LoyaltyPrograms | `Code,Name,KindCode,AirlineIata,AllianceCode,Tiers` (Tiers = JSON array) |

**Lounges Providers column example:**
```json
[{"providerCode":"AMEX","role":"Primary","minAllianceTierCode":null,"notes":null}]
```

**LoyaltyPrograms Tiers column example:**
```json
[{"code":"GOLD","name":"Gold","rank":1,"allianceCode":null,"allianceTierCode":null}]
```
