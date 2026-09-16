# Admin Enum Column Names Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the admin data browser render enum columns as their names, like every other admin surface already does.

**Architecture:** One branch in the Application query's row projection, beside the masking that already happens there. The repository stays a generic reader that knows nothing about presentation.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core, xUnit, `WebApplicationFactory` on the InMemory provider.

**Spec:** issue #392 and its scope-note comment.

## Global Constraints

- **Display only.** Nothing about sorting, filtering or the repository changes. `?filter=SyncMode:Fast` already works (`ConvertFilterValue` → `Enum.Parse(..., ignoreCase: true)`), and this change makes the display agree with it.
- **Convert at the Application edge**, beside masking in `GetBrowsableDataQueryHandler`. Never in `BrowsableDataRepository`: it is deliberately a generic paginated reader, and masking already lives at this boundary for the same reason.
- **No global `JsonStringEnumConverter`.** That would change every enum in every response across the whole API and alter the published contract; this wart is one endpoint's.
- **`null` stays `null`.** `Flight.SyncMode` is `FlightSyncMode?`.
- **Non-enum values are untouched** — numbers stay numbers, dates stay dates, strings stay strings.
- **Masking still wins.** A sensitive column renders as the placeholder whether or not it is an enum.
- **`spec/openapi.json` must not change:** the browser's rows are `StringObjectDictionaryPagedResult`, an untyped bag, so no schema names these columns.
- The build stays warning-free; commits use explicit paths; no paid API calls, no migration, nothing pushed.

---

## File Structure

- **Modify** `FlightAssistant.Application/Queries/Admin/Data/GetBrowsableDataQuery.cs` — the per-row projection.
- **Modify** `FlightAssistant.API.Tests/Integration/AdminDataEndpointTests.cs` — four tests.
- **Modify** `CLAUDE.md` — one sentence in the data-browser paragraph.

---

### Task 1: Render enum values as names

**Files:** the three above.

**Interfaces:**
- Consumes: `SensitiveProperties.IsSensitive`, `SensitiveProperties.RedactedPlaceholder`, the row dictionaries from `IBrowsableDataRepository.QueryAsync`.
- Produces: no new public surface — the endpoint's JSON values change from numbers to strings for seven columns.

- [ ] **Step 1: Convert in the projection**

In `GetBrowsableDataQuery.cs`, the private `Mask` method currently does one job. It now does two, so rename it and add the branch. Replace the whole method:

```csharp
        private static Dictionary<string, object?> Mask(BrowsableTable table, IReadOnlyDictionary<string, object?> row)
        {
            var masked = new Dictionary<string, object?>(row.Count);
            foreach (var (column, value) in row)
            {
                masked[column] = SensitiveProperties.IsSensitive(table.EntityType, column)
                    ? SensitiveProperties.RedactedPlaceholder
                    : value;
            }

            return masked;
        }
```

with:

```csharp
        /// <summary>
        /// Shapes one row for the response: masks sensitive columns, and renders enum values as their
        /// names (#392). An enum property arrives boxed from the repository's reflection projection, and
        /// <c>System.Text.Json</c> writes a boxed enum as its underlying number — so <c>SyncMode</c> read
        /// as <c>1</c> and audit <c>Action</c> as <c>2</c>, while every other admin surface
        /// (<c>AdminAuditEndpoints</c>, the health report, the verification endpoints) exposes the name.
        /// The browser's own filter already parses names, so the number was the odd one out in its own
        /// endpoint. Masking is checked first: a sensitive column renders as the placeholder whether or
        /// not it is an enum.
        /// </summary>
        private static Dictionary<string, object?> Project(BrowsableTable table, IReadOnlyDictionary<string, object?> row)
        {
            var projected = new Dictionary<string, object?>(row.Count);
            foreach (var (column, value) in row)
            {
                projected[column] = SensitiveProperties.IsSensitive(table.EntityType, column)
                    ? SensitiveProperties.RedactedPlaceholder
                    : value is Enum enumValue ? enumValue.ToString()
                    : value;
            }

            return projected;
        }
```

`value is Enum` is what keeps `null` null — a null never matches the pattern — and leaves every non-enum value exactly as it was.

Update the one call site, and the comment above it that says masking ships here, so it names both jobs:

```csharp
            // Masking and enum rendering ship here, not in the repository: every column the repository
            // handed back is checked against SensitiveProperties.IsSensitive before this method returns,
            // so the full value of a sensitive column never leaves the API from this endpoint, and an
            // enum renders as its name rather than its underlying number (#392). The repository stays
            // ignorant of both — it is a generic paginated reader, nothing about it is
            // admin-data-browser-specific.
            var items = rows.Select(row => Project(table, row)).ToList();
```

- [ ] **Step 2: Write the failing tests**

In `AdminDataEndpointTests.cs`, add four tests next to the existing table-data ones, using the file's exact idiom (verified at HEAD):

```csharp
        await SeedAsync(db => /* … */);

        var body = await GetRawBodyAsync(factory.CreateAdminClient(), "admin/data/flights?filter=…");

        var result = JsonSerializer.Deserialize<PagedResult<Dictionary<string, JsonElement>>>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var row = Assert.Single(result!.Items);
```

`GetRawBodyAsync(HttpClient, string url)` already asserts a 200 and returns the body string. Filter by a marker column so the seeded row is the single result, the way the existing tests do — the InMemory database is shared across the Integration collection.

Cover both EF conversion flavours and the nullable case:

1. **`GetTableData_EnumColumn_RendersTheName`** — seed a flight whose `SyncMode` is `FlightSyncMode.Fast` (`ApplySyncPlan`), GET `admin/data/flights?filter=Id:{id}`, and assert the row's `SyncMode` is `JsonValueKind.String` with value `"Fast"`. Asserting the kind matters as much as the value: a number would also stringify in a loose comparison.
2. **`GetTableData_NullEnumColumn_StaysNull`** — a flight with no sync plan: `JsonValueKind.Null`, not `"0"` and not `0`.
3. **`GetTableData_NonEnumColumns_AreUnchanged`** — on the same row, assert an integer column is still `JsonValueKind.Number` and a string column still `JsonValueKind.String`. This is the guard against "stringify everything".
4. **`GetTableData_EnumColumn_StillFilterableByItsName`** — `admin/data/flights?filter=SyncMode:Fast` returns the seeded row, proving the display now agrees with what the filter already accepted.

If seeding a flight with a sync plan is awkward, use `admin-audit-log` with a seeded `AdminAuditLog` (its `Action` is the string-converted flavour) for tests 1, 3 and 4 instead, and keep `flights` for the nullable case — the point is to cover one of each, not a specific table. Say which you chose in the report.

- [ ] **Step 3: Run them**

Run: `dotnet test FlightAssistant.API.Tests/FlightAssistant.API.Tests.csproj --filter "FullyQualifiedName~AdminDataEndpointTests"`
Expected: the four new tests PASS, and every pre-existing test in the class still PASSES — in particular the masking tests, which must be unaffected.

- [ ] **Step 4: Prove each test bites**

Break, watch the named test fail, restore with `mv … .bak` then `touch`. Record each break, its failing output and the restore.

1. Remove the `value is Enum enumValue` branch → `GetTableData_EnumColumn_RendersTheName` fails (a number comes back).
2. Convert unconditionally (`value?.ToString()` for every value) → `GetTableData_NonEnumColumns_AreUnchanged` fails.
3. Render the enum with its numeric format — `enumValue.ToString("D")` → `GetTableData_EnumColumn_RendersTheName` fails with `"1"` instead of `"Fast"`. This proves the assertion checks the *name*, not merely that the value became a string.

**A gap to record, not to close:** reordering the enum branch ahead of the sensitivity check would be unobservable, because **none of the seven enum columns is registered in `SensitiveProperties`** (checked at HEAD across all five entity types). So no test can prove masking still wins for an enum column. Don't invent a sensitive enum column to manufacture one — note it in the report, and the ordering stays correct by construction because the ternary tests sensitivity first.

- [ ] **Step 5: Document it**

In `CLAUDE.md`, in the paragraph beginning `**`BrowsableTables` says what is exposed; `SensitiveProperties` says what is masked.**`, add after the sentence about masking happening in the Application handler:

```markdown
The same projection renders enum columns as their **names** (#392) — `SyncMode` as `"Fast"`, audit `Action` as `"Update"` — because the repository projects materialised properties by reflection, so an enum arrives boxed and `System.Text.Json` would otherwise write its underlying number, unlike every other admin surface. The filter already parsed names (`ConvertFilterValue` → `Enum.Parse`), so this makes the grid agree with what it accepts. Sorting still orders by the underlying value in SQL — declaration order, not alphabetical — because sorting names in memory would break paging.
```

- [ ] **Step 6: Full check and commit**

Run: `dotnet test` (all four projects PASS) and `dotnet build --configuration Release` (0 warnings, 0 errors).
Run: `FlightSync__Enabled=false scripts/generate-openapi.sh && git diff --stat spec/openapi.json` — **must print nothing**.

```bash
git add FlightAssistant.Application/Queries/Admin/Data/GetBrowsableDataQuery.cs FlightAssistant.API.Tests/Integration/AdminDataEndpointTests.cs CLAUDE.md
git commit -m "fix: render admin data browser enum columns as names, not numbers (#392)"
```
