# A Program's Alliance Comes From Its Airline — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove `LoyaltyProgram.AllianceCode` so a program's alliance is its airline's, moving the alliance facts onto the airlines first so none is lost.

**Architecture:** The alliance facts currently live only on programs (21 of 25 seeded programs carry one; all 692 seeded airlines carry none). So the data moves down onto the airlines in two ways — by seed file for databases created later, by migration for databases that already exist — and only then does the column go, from the domain, the database and every contract. No read derives anything: after this change a program simply has no alliance of its own, and a consumer wanting one reads the program's airline.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, MediatR, FluentValidation, xUnit, Testcontainers (real `postgres:16-alpine`).

**Spec:** `docs/superpowers/specs/2026-09-19-program-alliance-from-airline-design.md`

## Global Constraints

- Work in this worktree: `/Users/salihalishov/Repositories/flight-assistant-backend-504`, branch `feature/504-program-alliance-from-airline`. The main checkout is in use by another session — never switch its branch.
- Base branch for the PR is **`development`**, not `main`.
- Alliance codes are exactly `SA` (Star Alliance), `OW` (oneworld), `ST` (SkyTeam) — the three rows in `alliances.v1.json`. `STAR` is not an alliance code. It must never appear as a real value in code, a seed row or a comment — the single exception is Task 3's migration test, which uses it on purpose as the junk code the foreign key has to refuse.
- Postgres spellings: tables are snake_case (`loyalty_programs`, `airlines`, `alliances`), columns are quoted PascalCase (`"AllianceCode"`, `"AirlineId"`, `"Id"`, `"Code"`).
- `airlines."AllianceCode"` carries a real foreign key to `alliances("Code")`. Any SQL that writes it must guarantee the code exists, or the statement fails.
- `LoyaltyTier.AllianceCode` and `LoyaltyTier.AllianceTierCode` are **out of scope** — lounge eligibility reads them. Do not touch them, including in `loyalty-programs.v1.json`, where tier-level `allianceCode` stays.
- Run a filtered test with `--filter "FullyQualifiedName~..."` only. `ClassName=` and `Name~` match nothing, run zero tests, and report success.
- Never hand-edit `spec/openapi.json`; regenerate it with `scripts/generate-openapi.sh` from the repo root.
- Commit messages end with:
  `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`

---

### Task 1: Move the alliance onto the airlines (seed files)

The program seed asserts an alliance for 21 airlines; the airline seed asserts none for any of the
692. This task moves those assertions to the rows that own them, and adds the two airlines the
program seed references but the airline seed does not contain.

`SeedCustomAsync` skips a dataset version already recorded in `seed_versions`, and
`PopulateAirlinesAsync` only ever *inserts* — it never updates a row that is already there. So an
in-place edit of `airlines.v1.json` reaches only databases created later (Task 2's migration covers
the ones that exist), while a **new** `airlines.v2.json` reaches both.

**Files:**
- Modify: `FlightAssistant.API/SeedData/Airline/airlines.v1.json` (21 rows gain `AllianceCode`)
- Create: `FlightAssistant.API/SeedData/Airline/airlines.v2.json`
- Test: `FlightAssistant.API.Tests/HostedServices/ReferenceDataSeederTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: seeded airlines carrying `AllianceCode`; airlines `CX` (Cathay Pacific, `OW`) and `KE` (Korean Air, `ST`) exist, so `CX-MPC` and `KE-SKY` link to an airline on a fresh seed. No C# signature changes.

- [ ] **Step 1: Write the failing test**

Add to `FlightAssistant.API.Tests/HostedServices/ReferenceDataSeederTests.cs` (the existing class already boots the real seeder against the real seed files on the EF in-memory provider — follow its shape):

```csharp
    /// <summary>
    /// #504: an alliance is an airline alliance, so the seed asserts it on the airline. The 21
    /// airlines the loyalty seed names carry their alliance (British Airways stands in for the
    /// list), and the two airlines that seed referenced without containing — Cathay Pacific and
    /// Korean Air — now exist, so their programs find an airline to link to.
    /// </summary>
    [Fact]
    public async Task StartAsync_GivesAirlinesTheirAllianceAndSeedsTheAirlinesProgramsReference()
    {
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddDbContext<FlightAssistantDbContext>(o => o.UseInMemoryDatabase(dbName));

        await using var provider = services.BuildServiceProvider();
        var seeder = new ReferenceDataSeeder(provider, NullLogger<ReferenceDataSeeder>.Instance);

        await seeder.StartAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();

        // airlines.v1.json — the alliance now lives on the airline.
        var britishAirways = await db.Airlines.SingleOrDefaultAsync(a => a.Iata == "BA");
        Assert.NotNull(britishAirways);
        Assert.Equal("OW", britishAirways!.AllianceCode);

        var lufthansa = await db.Airlines.SingleOrDefaultAsync(a => a.Iata == "LH");
        Assert.Equal("SA", lufthansa!.AllianceCode);

        // An airline with no alliance stays without one.
        var emirates = await db.Airlines.SingleOrDefaultAsync(a => a.Iata == "EK");
        Assert.Null(emirates!.AllianceCode);

        // airlines.v2.json — the two airlines the loyalty seed referenced but the airline seed lacked.
        var cathay = await db.Airlines.SingleOrDefaultAsync(a => a.Iata == "CX");
        Assert.NotNull(cathay);
        Assert.Equal("Cathay Pacific", cathay!.Name);
        Assert.Equal("OW", cathay.AllianceCode);

        var korean = await db.Airlines.SingleOrDefaultAsync(a => a.Iata == "KE");
        Assert.NotNull(korean);
        Assert.Equal("ST", korean!.AllianceCode);

        // Airlines seed before loyalty programs, so these two now link instead of logging a warning.
        var cathayProgram = await db.LoyaltyPrograms.SingleAsync(p => p.Code == "CX-MPC");
        Assert.Equal(cathay.Id, cathayProgram.AirlineId);

        var koreanProgram = await db.LoyaltyPrograms.SingleAsync(p => p.Code == "KE-SKY");
        Assert.Equal(korean.Id, koreanProgram.AirlineId);
    }
```

- [ ] **Step 2: Run it and watch it fail**

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-504
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj \
  --filter "FullyQualifiedName~StartAsync_GivesAirlinesTheirAllianceAndSeedsTheAirlinesProgramsReference"
```

Expected: FAIL. First assertion to go is `Assert.Equal("OW", britishAirways!.AllianceCode)` — actual `null`, because no airline row carries an alliance yet.

- [ ] **Step 3: Put the alliance on the 21 airline rows**

Derive the edit from the program seed rather than retyping it, so the two files cannot disagree.
Run from the repo root:

```bash
python3 - <<'PY'
import json, collections

progs = json.load(open('FlightAssistant.API/SeedData/LoyaltyProgram/loyalty-programs.v1.json'))
path  = 'FlightAssistant.API/SeedData/Airline/airlines.v1.json'
airs  = json.load(open(path))

wanted = {p['airlineIata']: p['allianceCode']
          for p in progs if p.get('allianceCode') and p.get('airlineIata')}

by_iata = collections.defaultdict(list)
for a in airs:
    by_iata[a.get('Iata')].append(a)

applied, absent = 0, []
for iata, code in sorted(wanted.items()):
    rows = by_iata.get(iata, [])
    if len(rows) != 1:
        absent.append((iata, code, len(rows)))
        continue
    rows[0]['AllianceCode'] = code
    applied += 1

with open(path, 'w') as f:
    json.dump(airs, f, indent=2, ensure_ascii=False)
    f.write('\n')

print(f'alliance written to {applied} airline rows')
print('no single matching airline row (expected CX and KE):', absent)
PY
```

Expected output:

```
alliance written to 21 airline rows
no single matching airline row (expected CX and KE): [('CX', 'OW', 0), ('KE', 'ST', 0)]
```

If any IATA other than `CX` and `KE` appears in that second line, stop and report it — the mapping
assumed by this plan has changed.

- [ ] **Step 4: Add the two missing airlines as a new seed version**

Create `FlightAssistant.API/SeedData/Airline/airlines.v2.json`:

```json
[
  { "Name": "Cathay Pacific", "Iata": "CX", "Icao": "CPA", "CountryCode": "HK", "AllianceCode": "OW" },
  { "Name": "Korean Air", "Iata": "KE", "Icao": "KAL", "CountryCode": "KR", "AllianceCode": "ST" }
]
```

`AirlineSeedDto` reads only `Iata`, `Icao`, `Name`, `CountryCode` and `AllianceCode`; the `Id`,
`Callsign` and `Active` fields on the v1 rows are ignored, so they are left out here.

- [ ] **Step 5: Confirm the new file reaches the test output**

The seeder reads `AppContext.BaseDirectory/SeedData/<dataset>`, and the seed files arrive there
through the Web SDK's `**/*.json` content glob — there is no per-file entry in
`FlightAssistant.API.csproj`, so a new file needs no project edit. Verify rather than assume:

```bash
dotnet build FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj -v q --nologo
ls FlightAssistant.API.Tests/bin/Debug/net10.0/SeedData/Airline/
```

Expected: both `airlines.v1.json` and `airlines.v2.json`. If v2 is missing, add it explicitly to
`FlightAssistant.API.csproj` as `<Content Include="SeedData\Airline\airlines.v2.json" CopyToOutputDirectory="PreserveNewest" />` and re-run.

- [ ] **Step 6: Run the test and the rest of the seeder tests**

```bash
dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj \
  --filter "FullyQualifiedName~ReferenceDataSeederTests"
```

Expected: PASS, including the pre-existing `StartAsync_PopulatesReferenceDataFromRealSeedFiles`.

- [ ] **Step 7: Commit**

```bash
git add FlightAssistant.API/SeedData/Airline/airlines.v1.json \
        FlightAssistant.API/SeedData/Airline/airlines.v2.json \
        FlightAssistant.API.Tests/HostedServices/ReferenceDataSeederTests.cs
git commit -m "$(cat <<'MSG'
feat(seed): put each airline's alliance on the airline (#504)

An alliance is an airline alliance, but the seed asserted it only on the
loyalty programs: all 692 airlines came up with no alliance while 21 of
25 programs carried one. The same assertions now sit on the airline rows
that own them, derived from the program seed so the two cannot disagree.

Cathay Pacific and Korean Air were referenced by CX-MPC and KE-SKY but
absent from the airline seed, which logged a warning and left both
programs unlinked. They arrive as airlines.v2.json rather than as rows in
v1: SeedCustomAsync skips a version already recorded in seed_versions, so
only a new version reaches a database that already exists.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
MSG
)"
```

---

### Task 2: Remove the column, and migrate the databases that hold it

**Why this is one task.** C# does not allow a smaller compiling step: the moment the property leaves
`LoyaltyProgram`, every layer that names it stops building, and `dotnet ef migrations add` reads the
compiled model, so the migration cannot be generated before the removal either. The task therefore
removes it everywhere and lands the migration in one commit. Task 3 then proves the migration's
behaviour against a real database.

**Files:**
- Modify: `FlightAssistant.Domain/Loyalty/Entities/LoyaltyProgram.cs` (field, `Create`, `CreateSeeded`, `Update`)
- Modify: `FlightAssistant.Infrastructure/Persistance/Configurations/Loyalty/LoyaltyProgramConfiguration.cs:17`
- Modify: `FlightAssistant.Application/Administration/BrowsableTables.cs:192`
- Modify: `FlightAssistant.Contracts/Responses/Loyalty/LoyaltyProgramDto.cs`, `Requests/Admin/Loyalty/CreateLoyaltyProgramRequest.cs`, `Requests/Admin/Loyalty/UpdateLoyaltyProgramRequest.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Loyalty/CreateLoyaltyProgramCommand.cs`, `UpdateLoyaltyProgramCommand.cs`
- Modify: `FlightAssistant.Application/Commands/Admin/Import/ImportLoyaltyProgramsCommand.cs`
- Modify: `FlightAssistant.Application/Queries/Admin/Export/ExportLoyaltyProgramsQuery.cs`
- Modify: `FlightAssistant.Application/Queries/Loyalty/GetLoyaltyProgramsQuery.cs`, `SearchLoyaltyProgramsQuery.cs`
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminLoyaltyEndpoints.cs`, `FlightAssistant.API/Endpoints/LoyaltyEndpoints.cs`
- Modify: `FlightAssistant.API/HostedServices/ReferenceDataSeeder.cs` (`LoyaltyProgramDto` seed record + the `CreateSeeded` call)
- Modify: `FlightAssistant.API/SeedData/LoyaltyProgram/loyalty-programs.v1.json` (program level only)
- Create: `FlightAssistant.Infrastructure/Persistance/Migrations/<timestamp>_RemoveLoyaltyProgramAllianceCode.cs` (+ `.Designer.cs`, + snapshot update)
- Test: `FlightAssistant.Application.Tests/Queries/Admin/Export/ExportLoyaltyProgramsQueryHandlerTests.cs`, `FlightAssistant.Application.Tests/Validators/ValidatorEdgeCaseTests.cs`, `FlightAssistant.Domain.Tests/Loyalty/LoyaltyProgramCreationTimeTests.cs`, `FlightAssistant.Application.Tests/Commands/Admin/AdminLoyaltyCommandHandlerTests.cs`, `AdminLoyaltyTierCommandHandlerTests.cs`, `FlightAssistant.Infrastructure.Tests/Persistance/Repositories/LoyaltyProgramNamesByAirlineRepositoryTests.cs`

**Interfaces:**
- Consumes: Task 1's seeded airline alliances.
- Produces, and every later task and the panel PR depend on these exact signatures:
  ```csharp
  // FlightAssistant.Domain/Loyalty/Entities/LoyaltyProgram.cs
  public static LoyaltyProgram Create(string code, string name, int kindId, int? airlineId = null);
  public static LoyaltyProgram CreateSeeded(string code, string name, int kindId, int? airlineId = null);
  public void Update(string code, string name, int kindId, int? airlineId);

  // FlightAssistant.Contracts
  public record LoyaltyProgramDto(int Id, string Code, string Name, string Kind, int? AirlineId,
      IReadOnlyList<LoyaltyTierDto> Tiers);
  public record CreateLoyaltyProgramRequest(string Code, string Name, int KindId, int? AirlineId);
  public record UpdateLoyaltyProgramRequest(string Code, string Name, int KindId, int? AirlineId);

  // FlightAssistant.Application
  public record CreateLoyaltyProgramCommand(string Code, string Name, int KindId, int? AirlineId) : IRequest<int>;
  public record UpdateLoyaltyProgramCommand(int Id, string Code, string Name, int KindId, int? AirlineId) : IRequest;
  public record LoyaltyProgramResult(int Id, string Code, string Name, string Kind, int? AirlineId,
      IReadOnlyList<LoyaltyTierResult> Tiers);
  public record LoyaltyProgramExportDto(string Code, string Name, string KindCode, string? AirlineIata,
      IReadOnlyList<LoyaltyTierExportDto> Tiers, DateTime? VerifiedAt, string? VerifiedBy, string? VerificationSource);
  public record LoyaltyProgramImportDto(string Code, string Name, string KindCode, string? AirlineIata,
      IReadOnlyList<LoyaltyTierImportDto> Tiers, DateTime? VerifiedAt = null, string? VerifiedBy = null,
      string? VerificationSource = null);
  ```
  `LoyaltyTierDto`, `LoyaltyTierResult`, `LoyaltyTierExportDto` and `LoyaltyTierImportDto` are **unchanged** — they keep `AllianceCode` and `AllianceTierCode`.

- [ ] **Step 1: Write the failing domain test**

The rule to pin is that the alliance is no longer part of a program. Replace the alliance assertion
in `FlightAssistant.Domain.Tests/Loyalty/LoyaltyProgramCreationTimeTests.cs` — the call at line ~43
reads `LoyaltyProgram.CreateSeeded("BA", "British Airways Executive Club", 2, airlineId: 7, allianceCode: "OW")`
and line 45 asserts `Assert.Equal("OW", program.AllianceCode)`. Make it:

```csharp
        var program = LoyaltyProgram.CreateSeeded("BA", "British Airways Executive Club", 2, airlineId: 7);

        Assert.Null(program.CreatedUtc);
        Assert.Equal("BA", program.Code);
        Assert.Equal(7, program.AirlineId);
        // #504: a program has no alliance of its own — it is the airline's. The property is gone,
        // so this type no longer compiles if anyone puts it back.
        Assert.Empty(typeof(LoyaltyProgram).GetProperties().Where(p => p.Name == "AllianceCode"));
```

Keep whatever else that test already asserts about `CreatedUtc`; only the alliance argument and
assertion change. Add `using System.Linq;` if the file lacks it.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test FlightAssistant.Domain.Tests/FlightAssistant.Domain.Tests.csproj \
  --filter "FullyQualifiedName~LoyaltyProgramCreationTime"
```

Expected: the build **succeeds** — `allianceCode` is an optional parameter, so dropping the argument
compiles fine — and the test then FAILS on the reflection assertion with
`Assert.Empty() Failure: Collection was not empty`, because the property is still there. Do not
proceed until you have seen that failure.

- [ ] **Step 3: Remove it from the domain entity**

In `FlightAssistant.Domain/Loyalty/Entities/LoyaltyProgram.cs`, delete the property and the
parameter from all three members. The result:

```csharp
        public int? AirlineId { get; private set; }             // FK -> airlines (optional)

        // #504: no AllianceCode here. An alliance is an airline alliance and this program's airline
        // carries it (airlines.AllianceCode, a foreign key into alliances); a second copy on the
        // program could only agree or be wrong, and it was free text with no foreign key at all.
        // A program with no airline has no alliance.
```

```csharp
        public static LoyaltyProgram Create(string code, string name, int kindId, int? airlineId = null) =>
            new()
            {
                Code = code,
                Name = name,
                KindId = kindId,
                AirlineId = airlineId,
                CreatedUtc = DateTime.UtcNow
            };

        /// <summary>A program from the seed files: as <see cref="Create"/>, but with no creation time.</summary>
        public static LoyaltyProgram CreateSeeded(string code, string name, int kindId, int? airlineId = null)
        {
            var program = Create(code, name, kindId, airlineId);
            program.CreatedUtc = null;
            return program;
        }

        public void Update(string code, string name, int kindId, int? airlineId)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new DomainException("Loyalty program code is required.");
            if (string.IsNullOrWhiteSpace(name))
                throw new DomainException("Loyalty program name is required.");

            Code = code;
            Name = name;
            KindId = kindId;
            AirlineId = airlineId;
        }
```

- [ ] **Step 4: Remove it from persistence and the browse columns**

`FlightAssistant.Infrastructure/Persistance/Configurations/Loyalty/LoyaltyProgramConfiguration.cs` — delete:

```csharp
            b.Property(x => x.AllianceCode).HasMaxLength(8);
```

`FlightAssistant.Application/Administration/BrowsableTables.cs` — delete this line from the
`loyalty-programs` column list (leave `nameof(LoyaltyTier.AllianceCode)` in the `loyalty-tiers`
list alone):

```csharp
                nameof(LoyaltyProgram.AllianceCode),
```

- [ ] **Step 5: Remove it from the contracts**

Delete `string? AllianceCode,` from `LoyaltyProgramDto`, and `string? AllianceCode` from
`CreateLoyaltyProgramRequest` and `UpdateLoyaltyProgramRequest`. `LoyaltyTierDto` in the same file
is untouched. Final shapes are in **Interfaces** above.

- [ ] **Step 6: Remove it from the commands and their validators**

In `CreateLoyaltyProgramCommand.cs`: drop `string? AllianceCode` from the record, change the handler
call to `LoyaltyProgram.Create(cmd.Code, cmd.Name, cmd.KindId, cmd.AirlineId)`, and delete the
validator rule `RuleFor(x => x.AllianceCode).MaximumLength(8).When(x => x.AllianceCode is not null);`.

In `UpdateLoyaltyProgramCommand.cs`: the same three edits, with the handler call becoming
`program.Update(cmd.Code, cmd.Name, cmd.KindId, cmd.AirlineId)`.

- [ ] **Step 7: Remove it from import, export and the two queries**

`ImportLoyaltyProgramsCommand.cs`:
- drop `string? AllianceCode,` from `LoyaltyProgramImportDto`;
- delete the validator rule `item.RuleFor(d => d.AllianceCode).MaximumLength(8).When(d => d.AllianceCode is not null);`;
- `LoyaltyProgram.Create(dto.Code, dto.Name, kind.Id, airlineId)`;
- `existing.Update(dto.Code, dto.Name, kind.Id, airlineId)`.

The tier rules and both `LoyaltyTier.Create(...)` calls in that handler stay exactly as they are.

`ExportLoyaltyProgramsQuery.cs`: drop `string? AllianceCode,` from `LoyaltyProgramExportDto` and
remove the `p.AllianceCode,` line from the projection.

`GetLoyaltyProgramsQuery.cs` and `SearchLoyaltyProgramsQuery.cs`: drop `string? AllianceCode,` from
`LoyaltyProgramResult` (declared once, in `GetLoyaltyProgramsQuery.cs`) and remove `p.AllianceCode,`
from both projections.

- [ ] **Step 8: Remove it from the endpoints**

`AdminLoyaltyEndpoints.cs` — three edits: the `new LoyaltyProgramDto(...)` projection loses
`p.AllianceCode,`; `new CreateLoyaltyProgramCommand(req.Code, req.Name, req.KindId, req.AirlineId)`
and `new UpdateLoyaltyProgramCommand(id, req.Code, req.Name, req.KindId, req.AirlineId)` lose
`req.AllianceCode`. The tier endpoints further down the file keep theirs.

`LoyaltyEndpoints.cs` — both `new LoyaltyProgramDto(...)` projections (in `programs/search` and
`programs`) lose `p.AllianceCode,`.

- [ ] **Step 9: Remove it from the seeder and the program seed file**

`FlightAssistant.API/HostedServices/ReferenceDataSeeder.cs`:
- the private `LoyaltyProgramDto` record loses `string? AllianceCode,`;
- the call becomes `LoyaltyProgram.CreateSeeded(dto.Code, dto.Name, kindId.Value, airlineId)`.

Strip the program-level key from the seed file, leaving every tier's alone:

```bash
python3 - <<'PY'
import json
path = 'FlightAssistant.API/SeedData/LoyaltyProgram/loyalty-programs.v1.json'
progs = json.load(open(path))
removed = 0
for p in progs:
    if 'allianceCode' in p:
        del p['allianceCode']
        removed += 1
tiers = sum(1 for p in progs for t in p.get('tiers', []) if 'allianceCode' in t)
with open(path, 'w') as f:
    json.dump(progs, f, indent=2, ensure_ascii=False)
    f.write('\n')
print(f'removed from {removed} programs; {tiers} tier-level allianceCode keys kept')
PY
```

Expected: `removed from 25 programs; 71 tier-level allianceCode keys kept` (the tier count is
informational — what matters is that it is not zero).

- [ ] **Step 10: Build, and follow the compiler through the tests**

```bash
dotnet build --configuration Release 2>&1 | grep -E "error|Build succeeded" | head -30
```

Fix each error by deleting the alliance argument or assertion. The full set, found beforehand:

| File | What to do |
|---|---|
| `FlightAssistant.Application.Tests/Queries/Admin/Export/ExportLoyaltyProgramsQueryHandlerTests.cs` | Two `LoyaltyProgram.Create(...)` calls lose their last argument; delete `Assert.Equal("OW", dto.AllianceCode);` |
| `FlightAssistant.Application.Tests/Commands/Admin/AdminLoyaltyCommandHandlerTests.cs` | Three `LoyaltyProgram.Create("BAEC", "BA Executive Club", 1, null, null)` calls become `(..., 1, null)` |
| `FlightAssistant.Application.Tests/Commands/Admin/AdminLoyaltyTierCommandHandlerTests.cs` | One such call, same edit |
| `FlightAssistant.Application.Tests/Validators/ValidatorEdgeCaseTests.cs` | Delete the whole `CreateLoyaltyProgram_AllianceCodeExceeds8_Fails` test (line ~419) — the rule it pins is gone. Any other `CreateLoyaltyProgramCommand(...)` construction loses its last argument |
| `FlightAssistant.Infrastructure.Tests/Persistance/Repositories/LoyaltyProgramNamesByAirlineRepositoryTests.cs` | `LoyaltyProgram.Create("OWRP", "oneworld Rewards", kind.Id, null, "OW")` → `(..., kind.Id, null)` |
| `FlightAssistant.Domain.Tests/Loyalty/LoyaltyProgramCreationTimeTests.cs` | Already done in Step 1 |

Re-run the build until it says `Build succeeded`.

- [ ] **Step 11: Add the import regression test**

An old import file still carrying a program-level `allianceCode` must keep importing —
`System.Text.Json` ignores an unmapped property — and the program must arrive without one. Add to
`FlightAssistant.Application.Tests/Commands/Admin/Import/ImportLoyaltyProgramsCommandHandlerTests.cs`,
following the fixture style already in that file:

```csharp
    /// <summary>
    /// #504: a program-level allianceCode is no longer part of the import contract. A file written
    /// before the change still imports — System.Text.Json ignores the unmapped property — and the
    /// program is created without an alliance, because the alliance belongs to its airline.
    /// </summary>
    [Fact]
    public async Task Handle_IgnoresAProgramLevelAllianceCodeFromAnOlderFile()
    {
        var json = """
        [
          { "code": "BA-EXEC", "name": "British Airways Executive Club", "kindCode": "FF",
            "airlineIata": "BA", "allianceCode": "OW", "tiers": [] }
        ]
        """;

        var items = JsonSerializer.Deserialize<List<LoyaltyProgramImportDto>>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Single(items);
        Assert.Equal("BA-EXEC", items[0].Code);
        Assert.Equal("BA", items[0].AirlineIata);
    }
```

Add `using System.Text.Json;` if the file lacks it. If that test file's existing tests build their
DTOs inline rather than from JSON, keep this one as written — the point is precisely that the JSON
shape still binds.

- [ ] **Step 12: Generate the migration**

```bash
dotnet ef migrations add RemoveLoyaltyProgramAllianceCode \
  --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
```

Expected: a new `<timestamp>_RemoveLoyaltyProgramAllianceCode.cs` whose `Up` drops the column, plus
its `.Designer.cs` and an updated `FlightAssistantDbContextModelSnapshot.cs`.

- [ ] **Step 13: Give the migration its data move**

Hand-edit the generated file so the alliance is copied onto the airlines **before** the column is
dropped. Follow the shape of `20260918225432_DropMembershipPointsExpiry.cs` — a summary saying what
is discarded and that `Down` returns the column empty:

```csharp
﻿using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlightAssistant.Infrastructure.Persistance.Migrations
{
    /// <summary>
    /// An alliance is an airline alliance, so it belongs on the airline (#504). The alliance facts
    /// currently live only on the programs, so this migration moves them down before dropping the
    /// column: each program's alliance is written onto its airline where the airline has none.
    ///
    /// <para>What it deliberately does not do. It never overwrites an alliance an admin set on the
    /// airline, and it cannot copy a code that names no row in <c>alliances</c> — that is the
    /// foreign key on <c>airlines.AllianceCode</c>, and such a value (free text was allowed here)
    /// is discarded with the column. Where two programs of one airline disagree, the lowest program
    /// id wins, so the result does not depend on row order.</para>
    ///
    /// <para><c>Down</c> brings the column back empty, not the values: by then the airline holds
    /// them.</para>
    /// </summary>
    public partial class RemoveLoyaltyProgramAllianceCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE airlines a
                SET "AllianceCode" = p."AllianceCode"
                FROM loyalty_programs p
                WHERE p."AirlineId" = a."Id"
                  AND a."AllianceCode" IS NULL
                  AND p."AllianceCode" IS NOT NULL
                  AND EXISTS (SELECT 1 FROM alliances al WHERE al."Code" = p."AllianceCode")
                  AND p."Id" = (
                      SELECT MIN(q."Id") FROM loyalty_programs q
                      WHERE q."AirlineId" = a."Id" AND q."AllianceCode" IS NOT NULL
                  );
                """);

            migrationBuilder.DropColumn(
                name: "AllianceCode",
                table: "loyalty_programs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllianceCode",
                table: "loyalty_programs",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }
    }
}
```

Keep whatever `using` and `Down` body the generator produced if they differ in detail — the column
type must match the snapshot (`character varying(8)`, `maxLength: 8`).

- [ ] **Step 14: Apply it to a local database**

```bash
docker compose up -d
CONNECTION_STRING="Host=localhost;Port=5432;Database=flightassistant;Username=fa;Password=fa" \
  dotnet ef database update --project FlightAssistant.Infrastructure --startup-project FlightAssistant.API
```

Expected: `Done.` Port 5432 is shared with other sessions' containers — if the database is already
in use, note it and rely on Task 3's test instead of blocking here.

- [ ] **Step 15: Run every test**

```bash
dotnet test 2>&1 | tail -20
```

Expected: all projects pass. Three suites matter most here, and none of them fails at compile time,
so they surface only in this full run:

- `LoungeEligibilityTests` (Domain.Tests) must pass **unchanged** — it reads the tier columns, which
  are out of scope, and is the check that this change did not reach lounge eligibility.
- `BrowsableTablesModelCompletenessTests` (Infrastructure.Tests) compares the browse column lists
  against the live EF model. It is what catches Step 4 being done by halves: the property removed
  but still listed, or listed but no longer mapped.
- `BrowsableTablesTests` and `GetBrowsableTablesQueryHandlerTests` (Application.Tests) pin the
  column lists themselves; update any assertion that spells out the `loyalty-programs` columns.

- [ ] **Step 16: Commit**

```bash
git add -A
git commit -m "$(cat <<'MSG'
feat(loyalty)!: take a program's alliance from its airline (#504)

loyalty_programs.AllianceCode is gone. An alliance is an airline
alliance and a program belongs to an airline, so the program-level copy
could only agree with the airline or be wrong -- and it was the weaker
column by far: free text capped at 8 characters, where
airlines.AllianceCode is a foreign key into alliances. Nothing read it
for behaviour; lounge eligibility reads the tier columns, which are
untouched.

The migration moves the data before dropping the column, writing each
program's alliance onto its airline where the airline has none. It never
overwrites an alliance set on the airline, discards a code that names no
alliance (the foreign key would refuse it), and breaks a tie between two
programs of one airline by lowest program id.

BREAKING: allianceCode leaves LoyaltyProgramDto on the public /loyalty
endpoints, and the program create/update requests. A client that wants a
program's alliance reads the program's airline.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
MSG
)"
```

---

### Task 3: Prove the migration against a real Postgres

The migration is the one part of this change that touches data, and its three guards are exactly
what a reviewer cannot check by reading. This test migrates a real database to the migration
**before** ours, inserts rows through raw SQL (the old schema still has the column), migrates
forward, and asserts what moved.

It does **not** join `PostgresCollection`: that fixture's container is already migrated to the
latest migration and reset by truncation, while this test needs a database at an earlier migration.
`PostgresCollectionMembershipTests` only constrains classes that carry the collection attribute, so
standing alone is legal — and it costs one extra container for the run.

**Files:**
- Create: `FlightAssistant.Infrastructure.Tests/Persistance/RemoveLoyaltyProgramAllianceCodeMigrationTests.cs`

**Interfaces:**
- Consumes: the migration from Task 2, found by name suffix `RemoveLoyaltyProgramAllianceCode`.
- Produces: nothing other tasks use.

- [ ] **Step 1: Write the failing test**

```csharp
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// #504: the migration that drops <c>loyalty_programs.AllianceCode</c> first copies each program's
/// alliance onto its airline. The three guards that cannot be read off the SQL are pinned here: an
/// alliance already on the airline is never overwritten, a code naming no alliance is discarded
/// rather than failing the migration, and a disagreement between two programs of one airline is
/// settled by lowest program id.
///
/// <para>This class owns its own container instead of joining <see cref="TestSupport.PostgresCollection"/>:
/// the shared fixture hands out a database already migrated to the latest migration, and this test
/// needs one at the migration before ours so it can write the column that is about to be dropped.</para>
/// </summary>
public sealed class RemoveLoyaltyProgramAllianceCodeMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private FlightAssistantDbContext _db = default!;
    private string _ourMigration = default!;
    private string _previousMigration = default!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        _db = new FlightAssistantDbContext(options);

        var all = _db.Database.GetMigrations().ToList();
        _ourMigration = all.Single(m => m.EndsWith("RemoveLoyaltyProgramAllianceCode"));
        _previousMigration = all[all.IndexOf(_ourMigration) - 1];
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Up_MovesEachProgramsAllianceOntoItsAirline()
    {
        var migrator = _db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(_previousMigration);

        await ExecuteAsync("""
            INSERT INTO alliances ("Code", "Name") VALUES ('OW', 'oneworld'), ('SA', 'Star Alliance');
            INSERT INTO countries ("Code", "Name") VALUES ('GB', 'United Kingdom') ON CONFLICT DO NOTHING;

            INSERT INTO airlines ("Iata", "Icao", "Name", "CountryCode", "AllianceCode")
            VALUES ('BA', 'BAW', 'British Airways', 'GB', NULL),      -- takes its program's alliance
                   ('LH', 'DLH', 'Lufthansa',       'GB', 'SA'),      -- keeps its own
                   ('XX', 'XXX', 'Junk Air',        'GB', NULL),      -- program's code is not an alliance
                   ('YY', 'YYY', 'Tie Air',         'GB', NULL);      -- two programs disagree

            INSERT INTO loyalty_program_kinds ("Code", "Name") VALUES ('FF', 'Frequent Flyer');

            INSERT INTO loyalty_programs ("Code", "Name", "KindId", "AirlineId", "AllianceCode")
            SELECT v.code, v.name, k."Id", a."Id", v.alliance
            FROM (VALUES
                    ('BA-EXEC', 'BA Executive Club', 'BA', 'OW'),
                    ('LH-MM',   'Miles & More',      'LH', 'OW'),
                    ('XX-ONE',  'Junk Program',      'XX', 'STAR')
                 ) AS v(code, name, iata, alliance)
            JOIN airlines a ON a."Iata" = v.iata
            CROSS JOIN loyalty_program_kinds k
            WHERE k."Code" = 'FF';
            """);

        // The tie case, inserted as two statements so the ids are unambiguous: the identity column
        // hands the first INSERT the lower id, and that row's alliance is the one the migration must
        // pick. Do NOT fold these into the VALUES list above -- an ORDER BY on an INSERT ... SELECT
        // does not promise identity values follow it, so which row won would rest on luck and on
        // how the codes happen to sort. Two statements promise it outright.
        await ExecuteAsync("""
            INSERT INTO loyalty_programs ("Code", "Name", "KindId", "AirlineId", "AllianceCode")
            SELECT 'YY-EARLIER', 'Tie, inserted first', k."Id", a."Id", 'OW'
            FROM airlines a CROSS JOIN loyalty_program_kinds k
            WHERE a."Iata" = 'YY' AND k."Code" = 'FF';
            """);

        await ExecuteAsync("""
            INSERT INTO loyalty_programs ("Code", "Name", "KindId", "AirlineId", "AllianceCode")
            SELECT 'YY-LATER', 'Tie, inserted second', k."Id", a."Id", 'SA'
            FROM airlines a CROSS JOIN loyalty_program_kinds k
            WHERE a."Iata" = 'YY' AND k."Code" = 'FF';
            """);

        await migrator.MigrateAsync(_ourMigration);

        // The plain case: the program's alliance is now the airline's.
        Assert.Equal("OW", await AllianceOfAsync("BA"));

        // An alliance already on the airline outranks anything a program claims.
        Assert.Equal("SA", await AllianceOfAsync("LH"));

        // 'STAR' names no alliance, so the foreign key forbids copying it. It goes with the column.
        Assert.Null(await AllianceOfAsync("XX"));

        // The earlier-inserted program holds the lower id, so its OW wins over the later SA.
        Assert.Equal("OW", await AllianceOfAsync("YY"));

        // And the column is gone.
        Assert.Equal(0L, await ScalarAsync("""
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_name = 'loyalty_programs' AND column_name = 'AllianceCode';
            """));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> AllianceOfAsync(string iata)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """SELECT "AllianceCode" FROM airlines WHERE "Iata" = @iata;""", connection);
        command.Parameters.AddWithValue("iata", iata);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : (string)value;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
```

- [ ] **Step 2: Run it**

```bash
docker info > /dev/null && dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj \
  --filter "FullyQualifiedName~RemoveLoyaltyProgramAllianceCodeMigrationTests"
```

Expected: PASS. Two failures are likely on the first run and both are the test's fault, not the
migration's — fix the test, not the SQL:

- a `NOT NULL` column the inserts do not supply (the seeded schema may require more of `countries`,
  `airlines` or `loyalty_program_kinds` than this fixture writes). Read the error and add the column.
- `Assert.Equal("OW", …)` for `YY` returning `SA`. That is a real migration bug, not a test bug:
  `YY-EARLIER` holds the lower id, so `MIN("Id")` must select its `OW`. Fix the SQL in the migration,
  not the assertion.

If `docker info` fails, Docker is not running — start it; CI requires it too (`ci.yml` verifies
Docker before the Testcontainers-backed project).

- [ ] **Step 3: Confirm the shared-container guard still passes**

```bash
dotnet test FlightAssistant.Infrastructure.Tests/FlightAssistant.Infrastructure.Tests.csproj \
  --filter "FullyQualifiedName~PostgresCollectionMembershipTests"
```

Expected: PASS — the new class carries no `[Collection]` attribute, so it is not an offender.

- [ ] **Step 4: Commit**

```bash
git add FlightAssistant.Infrastructure.Tests/Persistance/RemoveLoyaltyProgramAllianceCodeMigrationTests.cs
git commit -m "$(cat <<'MSG'
test(migration): pin the alliance copy-up against a real Postgres (#504)

The migration's three guards are the part of #504 a reviewer cannot
check by reading: an alliance already on the airline is not overwritten,
a code naming no alliance is discarded rather than failing the run, and
two programs of one airline are settled by lowest program id.

The test migrates to the migration before ours, writes the column
through raw SQL while it still exists, migrates forward and asserts what
moved. It owns its container rather than joining PostgresCollection,
whose fixture hands out a database already at the latest migration.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
MSG
)"
```

---

### Task 4: Regenerate the spec, verify, and open the PR

**Files:**
- Modify: `spec/openapi.json` (generated)

**Interfaces:**
- Consumes: Tasks 1-3.
- Produces: a committed OpenAPI document matching the code, which the panel's orval client is generated from.

- [ ] **Step 1: Regenerate the OpenAPI document**

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-504
scripts/generate-openapi.sh
git diff --stat spec/openapi.json
```

Expected: `allianceCode` disappears from the loyalty **program** schemas only. Verify that, rather
than trusting the diff's size:

```bash
git diff spec/openapi.json | grep -c allianceCode          # removals only
grep -c allianceCode spec/openapi.json                     # tier schemas still have it
```

Expected: the second number is greater than zero — `LoyaltyTierDto` keeps its `allianceCode` and
`allianceTierCode`. If it is zero, a tier contract was changed by mistake; go back and fix it.

- [ ] **Step 2: Full verification before any success claim**

```bash
dotnet build --configuration Release 2>&1 | tail -3
dotnet test 2>&1 | tail -20
```

Expected: `Build succeeded`, every test project passing. Paste the real output into the PR
description's verification section — no claim of passing without it.

- [ ] **Step 3: Commit and push**

```bash
git add spec/openapi.json
git commit -m "$(cat <<'MSG'
chore(spec): regenerate openapi after the program alliance removal (#504)

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
MSG
)"
git push -u origin feature/504-program-alliance-from-airline
```

- [ ] **Step 4: Open the PR against `development`**

```bash
gh pr create --base development \
  --title "Take a loyalty program's alliance from its airline (#504)" \
  --body "$(cat <<'BODY'
Closes #504.

An alliance is an airline alliance, and a loyalty program belongs to an airline — so
`loyalty_programs.AllianceCode` could only ever agree with the airline or be wrong. It was also the
weaker of the two columns: free text capped at 8 characters, where `airlines.AllianceCode` is a
foreign key into `alliances` validated on create, update and import (#449). The drift that allowed
was already in the repo — the entity comment and both panel forms offered `"STAR" / OW / ST`, while
the real codes are `SA / OW / ST`.

Nothing read the program-level column for behaviour. Lounge eligibility reads
`LoyaltyTier.AllianceCode`/`AllianceTierCode`, which this PR does not touch, and verification never
compared a program's alliance.

## The data moves before the column goes

The alliance facts lived **only** on programs: all 692 seeded airlines had no alliance, 21 of 25
seeded programs had one. So:

| Change | Fresh database | Existing database |
|---|---|---|
| `AllianceCode` added to 21 rows of `airlines.v1.json` | applied | skipped — Airline v1 is already in `seed_versions`; the migration covers these rows |
| New `airlines.v2.json` — Cathay Pacific and Korean Air | applied | applied — a version no database has seen |
| Migration copy-up | nothing to copy | moves each program's alliance onto its airline |

Cathay Pacific and Korean Air were referenced by `CX-MPC` and `KE-SKY` but absent from the airline
seed, which logged a warning and left both programs unlinked.

The migration has four properties worth reviewing: an alliance already set on the airline is never
overwritten; a code naming no row in `alliances` cannot be copied (the foreign key forbids it) and is
discarded with the column; a tie between two programs of one airline is settled by lowest program id;
and `Down` returns the column empty, not the values. `RemoveLoyaltyProgramAllianceCodeMigrationTests`
pins all four against a real `postgres:16-alpine`.

## Breaking

`allianceCode` leaves `LoyaltyProgramDto` — which is on the **public** `/loyalty` endpoints, not only
the admin ones — and leaves the program create/update requests. It is removed rather than kept as a
value computed from the airline, because keeping it would leave the contract still claiming a program
has an alliance of its own. **This assumes the admin panel is the only client of those endpoints.**
If another client reads the field, say so and I will restore it as a computed property.

An import file that still carries a program-level `allianceCode` keeps importing — `System.Text.Json`
ignores the unmapped property — and the program arrives without one.

## Known manual step

On a database that already exists, `CX-MPC` and `KE-SKY` end up unlinked: their airlines are absent
when the migration runs, so those two alliance values are lost, and the loyalty seeder returns early
for a program code that already exists. `airlines.v2.json` inserts the two airlines on the next boot;
an admin links the two programs from the panel. The panel's *Not linked* signal (next issue) is what
makes them findable.

## Follow-ups

- Panel issue, opened when this merges: drop the alliance input and column, regenerate the orval
  client, signal a program with no airline.
- `LoyaltyTier.AllianceCode` — the same duplication one level down, on the one path that reads it.

## Verification

<paste the exact output of `dotnet build --configuration Release` and `dotnet test` from Step 2 here>

🤖 Generated with [Claude Code](https://claude.com/claude-code)
BODY
)"
```

The one thing to fill in is the verification block — paste the real command output, never a summary
of it.

- [ ] **Step 5: Check CI, not just the local run**

```bash
gh pr checks --watch
```

Expected: every check green. The OpenAPI drift check regenerates the spec and fails the build if it
differs from the committed file byte-for-byte, so a stale `spec/openapi.json` surfaces here. Do not
call the PR ready until this passes.

- [ ] **Step 6: Stop**

Do not start the panel change. It is a separate issue, opened after this PR merges, and its PR never
merges alongside this one. Report back with the PR link and the two follow-ups: the panel issue and
the `LoyaltyTier.AllianceCode` issue.
