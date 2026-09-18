# Filtering a List by More Than One Criterion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let every admin list be narrowed by several criteria at once, ANDed — "airlines in Turkey whose name contains *air*", "unverified airlines in Turkey".

**Architecture:** The `filter` query parameter on `GET /admin/data/{table}` becomes repeatable; each occurrence keeps today's `column:value` grammar and today's matching rules, and the repository folds them into chained `Where` calls, which EF ANDs into one statement. `ApplyFilter` and `ConvertFilterValue` — where all the per-value semantics live — are not touched. In the panel, applied criteria become removable chips above the existing filter row.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, MediatR, FluentValidation, EF Core + Postgres, xUnit, NSubstitute (backend); React 19, React Router, TanStack Query, orval, Vitest + Testing Library (panel).

**Spec:** `docs/superpowers/specs/2026-09-19-multi-criteria-filters-design.md` (issue #476)

## Global Constraints

- **`ApplyFilter` and `ConvertFilterValue` in `BrowsableDataRepository` are not modified.** Every per-value semantic — case-insensitive contains, `*` as "has a value" (#385), `>=` on dates (#334), enums, bools, nullable columns — keeps its current implementation and its current tests. Only the *number of calls* changes. If a task seems to need an edit inside either method, stop and report.
- **One `filter=` must keep behaving exactly as today.** Six callers build single-filter URLs (dashboard stat tiles, needs-attention, the pending-queue link, traveler → traveler-flights, flight → flight-status-events, the health report's direct API call), plus any URL a user has bookmarked. None of them may be edited by this work.
- **Criteria combine with AND only.** No OR, no grouping, no per-criterion connector.
- **Cap: at most 10 criteria**, 400 beyond it.
- **`spec/openapi.json` is regenerated, never hand-edited** — `scripts/generate-openapi.sh` from the backend repo root. The expected diff is confined to the `filter` parameter on `GET /admin/data/{table}` and that operation's description. Anything else moving means stop and report.
- **The build stays warning-free**; commits use explicit paths; no migration, no paid API calls, nothing pushed without being asked.
- **Prove each test catches its bug.** After a task's tests pass, break the code the test guards — a break *derived from the code as written*, not from a guess recorded here — show the test failing, then restore. If a break passes, the test is what is wrong: add the one that fails.
- **Two PRs, in order.** Tasks 1–4 are the backend PR; Tasks 5–7 are the panel PR, which merges only after the backend PR is merged, because the regenerated client describes a `filter` the deployed API does not accept until then.

---

## File Structure

**Backend** (`flight-assistant-backend`, branch off `development`):

- **Modify** `FlightAssistant.Application/Interfaces/Repositories/IBrowsableDataRepository.cs` — `QueryAsync` takes a criteria list.
- **Modify** `FlightAssistant.Infrastructure/Persistance/Repositories/BrowsableDataRepository.cs` — fold the list in `QueryGenericAsync` and `BuildQueryStringGeneric`; `ApplyFilter` untouched.
- **Modify** `FlightAssistant.Application/Queries/Admin/Data/GetBrowsableDataQuery.cs` — `Filters` list, `ParsedFilters`, per-criterion validation, the cap, handler pass-through.
- **Modify** `FlightAssistant.API/Endpoints/Admin/AdminDataEndpoints.cs` — `string[]? filter` and the OpenAPI description.
- **Modify** `FlightAssistant.Infrastructure.Tests/Persistance/Repositories/BrowsableDataRepositoryTests.cs` — ~20 mechanical call-site updates, plus new multi-criteria tests.
- **Modify** `FlightAssistant.Application.Tests/Queries/Admin/Data/GetBrowsableDataQueryHandlerTests.cs` — the NSubstitute argument matchers, plus a pass-through test.
- **Create** `FlightAssistant.Application.Tests/Queries/Admin/Data/GetBrowsableDataQueryValidatorTests.cs` — the per-criterion rules and the cap.
- **Modify** `FlightAssistant.API.Tests/Integration/AdminDataEndpointTests.cs` — end-to-end multi-criteria tests.
- **Modify** `spec/openapi.json` — regenerated.

**Panel** (`flight-assistant-admin-panel`, branch off `main`):

- **Modify** `openapi/openapi.json`, `src/api/generated/**` — synced and regenerated, not hand-edited.
- **Modify** `src/pages/data/DataBrowserPage.tsx` — read `getAll('filter')`, `updateView({ filters })`, the chips, the Add-filter form, the Verified toggle.
- **Create** `src/pages/data/appliedFilters.ts` — the pure criteria-list helpers (parse, label, add, remove), so the page file does not grow another block of logic.
- **Create** `src/pages/data/appliedFilters.test.ts` — unit tests for those helpers.
- **Modify** `src/pages/data/DataBrowserPage.test.tsx` — the fake API takes an array; existing single-filter assertions become one-element arrays; new multi-criteria tests.
- **Modify** `src/pages/data/VerifyReferenceData.test.tsx` — the Verified toggle's assertions.

---

# Phase 1 — Backend

### Task 1: The repository takes a list of criteria

**Files:**
- Modify: `FlightAssistant.Application/Interfaces/Repositories/IBrowsableDataRepository.cs`
- Modify: `FlightAssistant.Infrastructure/Persistance/Repositories/BrowsableDataRepository.cs`
- Modify: `FlightAssistant.Application/Queries/Admin/Data/GetBrowsableDataQuery.cs` (handler call site only)
- Test: `FlightAssistant.Infrastructure.Tests/Persistance/Repositories/BrowsableDataRepositoryTests.cs`

**Interfaces:**
- Consumes: `BrowsableTable`, `ApplyFilter<TEntity>` (unchanged), `ApplySort<TEntity>` (unchanged).
- Produces: `IBrowsableDataRepository.QueryAsync(BrowsableTable table, int page, int pageSize, string? sortColumn, bool sortDescending, IReadOnlyList<(string Column, string Value)> filters, CancellationToken ct = default)` and `BrowsableDataRepository.BuildQueryString(BrowsableTable table, string? sortColumn, bool sortDescending, IReadOnlyList<(string Column, string Value)> filters)`. Tasks 2 and 3 call the first; nothing else in the codebase calls either.

- [ ] **Step 1: Write the failing tests**

In `BrowsableDataRepositoryTests.cs`, after the existing string-filter tests, add:

```csharp
    // ── Several criteria, ANDed ─────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_TwoFiltersOnDifferentColumns_MatchesOnlyRowsSatisfyingBoth()
    {
        await using var db = CreateDbContext();
        var wanted = Traveler.Register($"{Guid.NewGuid():N}@example.com", "Ada Lovelace");
        var wrongName = Traveler.Register($"{Guid.NewGuid():N}@example.com", "Bob Smith");
        db.Travelers.AddRange(wanted, wrongName);
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).QueryAsync(
            TravelersTable, page: 1, pageSize: 50,
            sortColumn: null, sortDescending: false,
            filters: [(nameof(Traveler.DisplayName), "ada"), (nameof(Traveler.Id), wanted.Id.ToString())]);

        Assert.Equal(1, total);
        Assert.Equal("Ada Lovelace", Assert.Single(items)[nameof(Traveler.DisplayName)]);
    }

    /// <summary>
    /// Two criteria on the <em>same</em> column AND rather than the last one winning — the failure
    /// this shape invites is a fold that overwrites instead of composing, which a test using two
    /// different columns would not notice.
    /// </summary>
    [Fact]
    public async Task QueryAsync_TwoFiltersOnTheSameColumn_RequireBothSubstrings()
    {
        await using var db = CreateDbContext();
        db.Travelers.AddRange(
            NewTraveler("Ada Lovelace"),
            NewTraveler("Ada Byron"));
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).QueryAsync(
            TravelersTable, page: 1, pageSize: 50,
            sortColumn: null, sortDescending: false,
            filters: [(nameof(Traveler.DisplayName), "ada"), (nameof(Traveler.DisplayName), "love")]);

        Assert.Equal(1, total);
        Assert.Equal("Ada Lovelace", Assert.Single(items)[nameof(Traveler.DisplayName)]);
    }

    /// <summary>
    /// Total is the count of rows matching <em>every</em> criterion — the classic bug in this shape
    /// is a count taken after only the first filter, or before the fold entirely.
    /// </summary>
    [Fact]
    public async Task QueryAsync_TwoFilters_CountsTheRowsMatchingBoth()
    {
        await using var db = CreateDbContext();
        db.Travelers.AddRange(
            NewTraveler("Ada Lovelace"),
            NewTraveler("Ada Byron"),
            NewTraveler("Grace Hopper"));
        await db.SaveChangesAsync();

        var (_, total) = await Repository(db).QueryAsync(
            TravelersTable, page: 1, pageSize: 1,
            sortColumn: null, sortDescending: false,
            filters: [(nameof(Traveler.DisplayName), "ada"), (nameof(Traveler.DisplayName), "byron")]);

        Assert.Equal(1, total);
    }

    [Fact]
    public void BuildQueryString_TwoFilters_PutsBothPredicatesInOneStatement()
    {
        using var db = CreateDbContext();

        var sql = Repository(db).BuildQueryString(
            TravelersTable, sortColumn: null, sortDescending: false,
            filters: [(nameof(Traveler.DisplayName), "ada"), (nameof(Traveler.DisplayName), "love")]);

        Assert.Contains("WHERE", sql);
        Assert.Contains("AND", sql);
        Assert.Equal(2, Regex.Matches(sql, "ILIKE|lower\\(", RegexOptions.IgnoreCase).Count);
    }
```

Add `using System.Text.RegularExpressions;` to the file's usings if it is not already there.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-476
dotnet test FlightAssistant.Infrastructure.Tests --filter "FullyQualifiedName~BrowsableDataRepositoryTests"
```

Expected: **compile errors** — `QueryAsync` and `BuildQueryString` have no `filters` parameter. That is the intended failure for a signature change; do not paper over it by adding an overload.

- [ ] **Step 3: Change the interface**

In `IBrowsableDataRepository.cs`, replace the `filterColumn` and `filterValue` parameters and their two `<param>` blocks with one:

```csharp
        /// <param name="filters">
        /// Zero or more <c>(Column, Value)</c> criteria, **combined with AND** — every criterion
        /// must match for a row to be returned. Each column name must already be validated against
        /// <c>BrowsableTables.IsColumnExposed</c> and confirmed non-sensitive by the caller, exactly
        /// as the single filter was: this method does not repeat that check. Repeats of the same
        /// column are legitimate and AND like any other pair. Each value is the raw string as
        /// received from the client — exact match for a non-string column, case-insensitive
        /// <c>contains</c> for a string column, translated to SQL rather than evaluated
        /// client-side — and is converted internally to the column's real CLR type; a value that
        /// will not parse throws a 400 <c>DomainException</c> rather than surfacing as a 500. An
        /// empty list is "no filter". Unlike <paramref name="sortColumn"/>, each column <em>is</em>
        /// re-checked explicitly via reflection and throws
        /// <see cref="InvalidOperationException"/> for a name that doesn't exist on the entity —
        /// defense-in-depth alongside the caller's own allowlist check, not a substitute for it.
        /// </param>
        Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Items, int Total)> QueryAsync(
            BrowsableTable table,
            int page,
            int pageSize,
            string? sortColumn,
            bool sortDescending,
            IReadOnlyList<(string Column, string Value)> filters,
            CancellationToken ct = default);
```

In the `<summary>` above it, change "`sortColumn` and `filterColumn` must already be validated" to "`sortColumn` and every column in `filters` must already be validated", and the trailing cross-reference "see `sortColumn` and `filterColumn` below" to "see `sortColumn` and `filters` below".

- [ ] **Step 4: Fold the list in the repository**

In `BrowsableDataRepository.cs`, `QueryAsync` becomes:

```csharp
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Items, int Total)> QueryAsync(
            BrowsableTable table,
            int page,
            int pageSize,
            string? sortColumn,
            bool sortDescending,
            IReadOnlyList<(string Column, string Value)> filters,
            CancellationToken ct = default) =>
            MakeGenericQuery<Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, int)>>(
                table.EntityType, nameof(QueryGenericAsync),
                table, page, pageSize, sortColumn, sortDescending, filters, ct);
```

In `QueryGenericAsync<TEntity>`, replace the `filterColumn`/`filterValue` parameters with
`IReadOnlyList<(string Column, string Value)> filters` and the single `if` with the fold:

```csharp
            IQueryable<TEntity> query = _db.Set<TEntity>().AsNoTracking();

            // Chained Where calls: EF ANDs them into one statement, which is exactly the required
            // semantics — every criterion must match. ApplyFilter is called once per criterion and
            // is itself unchanged, so each criterion keeps the matching rules it has always had.
            foreach (var (column, value) in filters)
                query = ApplyFilter(query, column, value);

            // Total must be counted after the filters above and before Skip/Take — the classic bug
            // in this shape is a Total computed against the unfiltered table.
            var total = await query.CountAsync(ct);
```

Make the same two changes to `BuildQueryString` and `BuildQueryStringGeneric<TEntity>`:

```csharp
        internal string BuildQueryString(
            BrowsableTable table, string? sortColumn, bool sortDescending,
            IReadOnlyList<(string Column, string Value)> filters) =>
            MakeGenericQuery<string>(
                table.EntityType, nameof(BuildQueryStringGeneric), sortColumn, sortDescending, filters);

        private string BuildQueryStringGeneric<TEntity>(
            string? sortColumn, bool sortDescending,
            IReadOnlyList<(string Column, string Value)> filters) where TEntity : class
        {
            IQueryable<TEntity> query = _db.Set<TEntity>().AsNoTracking();
            foreach (var (column, value) in filters)
                query = ApplyFilter(query, column, value);
            query = ApplySort(query, sortColumn, sortDescending);
            return query.ToQueryString();
        }
```

Leave `ApplyFilter`, `ConvertFilterValue`, `ApplySort`, `PropertyKeySelector`, `GetValueGenericAsync` and `CountGenericAsync` exactly as they are.

- [ ] **Step 5: Adapt the handler so the solution compiles**

In `GetBrowsableDataQuery.cs`'s handler, the query record still carries one `Filter` at this point (Task 2 changes that). Wrap it:

```csharp
            var filter = request.ParsedFilter;

            var (rows, total) = await _repository.QueryAsync(
                table,
                request.Paging.Page,
                request.Paging.PageSize,
                request.SortColumn,
                request.SortDescending,
                filter is null ? [] : [(filter.Value.Column, filter.Value.Value)],
                ct);
```

- [ ] **Step 6: Update the existing repository test call sites**

About twenty calls in `BrowsableDataRepositoryTests.cs` pass `filterColumn:`/`filterValue:`. These updates are **mechanical and must stay mechanical** — a test that changes meaning while its signature is being updated is a regression hidden in churn:

- `filterColumn: null, filterValue: null` → `filters: []`
- `filterColumn: null` alone → `filters: []`
- `filterColumn: "X", filterValue: "y"` → `filters: [("X", "y")]`
- the same two renames in every `BuildQueryString(...)` call.

Do not change any assertion, any seeded row, or any test name in this step.

- [ ] **Step 7: Update the handler test's argument matchers**

In `GetBrowsableDataQueryHandlerTests.cs`, the NSubstitute setup matches the old arity:

```csharp
        _repository.QueryAsync(
                Arg.Any<BrowsableTable>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<IReadOnlyList<(string Column, string Value)>>(),
                Arg.Any<CancellationToken>())
            .Returns((new[] { row }, 1));
```

Apply that to every such setup in the file. Assertions stay as they are.

- [ ] **Step 8: Run the tests to verify they pass**

```bash
dotnet build && \
dotnet test FlightAssistant.Infrastructure.Tests --filter "FullyQualifiedName~BrowsableDataRepositoryTests" && \
dotnet test FlightAssistant.Application.Tests --filter "FullyQualifiedName~GetBrowsableDataQuery"
```

Expected: PASS, with no new warnings.

- [ ] **Step 9: Prove the new tests bite**

Derive the break from the fold you just wrote — for example, replace the `foreach` with a single `ApplyFilter` call on `filters[^1]` (last criterion wins), or move `CountAsync` above the fold. Run the three `QueryAsync_TwoFilters*` tests and `BuildQueryString_TwoFilters_PutsBothPredicatesInOneStatement`, show the failure, then restore. If a break passes, add the test that fails before moving on.

- [ ] **Step 10: Commit**

```bash
git add FlightAssistant.Application/Interfaces/Repositories/IBrowsableDataRepository.cs \
        FlightAssistant.Infrastructure/Persistance/Repositories/BrowsableDataRepository.cs \
        FlightAssistant.Application/Queries/Admin/Data/GetBrowsableDataQuery.cs \
        FlightAssistant.Infrastructure.Tests/Persistance/Repositories/BrowsableDataRepositoryTests.cs \
        FlightAssistant.Application.Tests/Queries/Admin/Data/GetBrowsableDataQueryHandlerTests.cs
git commit -m "refactor(data): read a browsable table through a list of filters (#476)"
```

---

### Task 2: The query, the endpoint and per-criterion validation

**Files:**
- Modify: `FlightAssistant.Application/Queries/Admin/Data/GetBrowsableDataQuery.cs`
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminDataEndpoints.cs:44` (the `string? filter` parameter) and `:63` (the `Send` call)
- Create: `FlightAssistant.Application.Tests/Queries/Admin/Data/GetBrowsableDataQueryValidatorTests.cs`
- Modify: `FlightAssistant.API.Tests/Integration/AdminDataEndpointTests.cs`

**Interfaces:**
- Consumes: `QueryAsync(..., IReadOnlyList<(string Column, string Value)> filters, ...)` from Task 1.
- Produces: `GetBrowsableDataQuery(string Table, PagedRequest Paging, string? Sort = null, IReadOnlyList<string>? Filters = null)` with `IReadOnlyList<(string Column, string Value)> ParsedFilters`. Task 3 adds the cap rule to the same validator.

- [ ] **Step 1: Write the failing validator tests**

Create `FlightAssistant.Application.Tests/Queries/Admin/Data/GetBrowsableDataQueryValidatorTests.cs`:

```csharp
using FlightAssistant.Application.Queries.Admin.Data;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Domain.Travelers.Entities;

namespace FlightAssistant.Application.Tests.Queries.Admin.Data;

/// <summary>
/// <see cref="GetBrowsableDataQueryValidator"/> with several criteria in play (#476). Each rule is
/// per-criterion, and each message names the criterion it is about: "invalid filter column" alone
/// does not say which of three filters to fix.
/// </summary>
public class GetBrowsableDataQueryValidatorTests
{
    private readonly GetBrowsableDataQueryValidator _validator = new();

    private static GetBrowsableDataQuery Query(params string[] filters) =>
        new("travelers", new PagedRequest(), Filters: filters);

    [Fact]
    public void SeveralValidCriteria_AreValid()
    {
        var result = _validator.Validate(Query(
            $"{nameof(Traveler.DisplayName)}:ada",
            $"{nameof(Traveler.CreatedUtc)}:>=2026-09-01"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void NoFiltersAtAll_IsValid()
    {
        Assert.True(_validator.Validate(new GetBrowsableDataQuery("travelers", new PagedRequest())).IsValid);
    }

    [Fact]
    public void OneCriterionWithNoColon_IsRejected_AndTheMessageNamesIt()
    {
        var result = _validator.Validate(Query($"{nameof(Traveler.DisplayName)}:ada", "NotAFilter"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("NotAFilter"));
    }

    [Fact]
    public void OneCriterionNamingAnUnknownColumn_IsRejected_AndTheMessageNamesThatColumn()
    {
        var result = _validator.Validate(Query($"{nameof(Traveler.DisplayName)}:ada", "Cuntry:TR"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("Cuntry"));
    }

    [Fact]
    public void ACriterionOnASensitiveColumn_IsRejected()
    {
        var result = _validator.Validate(Query($"{nameof(Traveler.Email)}:a"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("sensitive"));
    }

    /// <summary>
    /// Two bad criteria produce two errors, not one: fixing the first and resubmitting only to be
    /// told about the second is the experience this avoids.
    /// </summary>
    [Fact]
    public void EveryFailingCriterion_IsReported()
    {
        var result = _validator.Validate(Query("Cuntry:TR", "Naem:air"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("Cuntry"));
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("Naem"));
    }

    [Fact]
    public void AValueContainingColons_KeepsThemAllInTheValue()
    {
        var query = Query($"{nameof(Traveler.CreatedUtc)}:2026-09-01T12:30:00Z");

        var parsed = Assert.Single(query.ParsedFilters);
        Assert.Equal(nameof(Traveler.CreatedUtc), parsed.Column);
        Assert.Equal("2026-09-01T12:30:00Z", parsed.Value);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test FlightAssistant.Application.Tests --filter "FullyQualifiedName~GetBrowsableDataQueryValidatorTests"
```

Expected: **compile error** — `GetBrowsableDataQuery` has no `Filters` parameter and no `ParsedFilters`.

- [ ] **Step 3: Make the query carry a list**

In `GetBrowsableDataQuery.cs`, replace the `Filter` parameter, its `<param>` doc and `ParsedFilter`:

```csharp
    /// <param name="Filters">
    /// Zero or more criteria, each <c>column:value</c>, **combined with AND** — every one must
    /// match. On the wire this is the <c>filter</c> query parameter, repeated:
    /// <c>?filter=Country:TR&amp;filter=Name:air</c>. A single <c>filter</c> is a list of one and
    /// behaves exactly as it always has.
    ///
    /// <para>Per criterion, unchanged: exact match for a non-string column, case-insensitive
    /// <c>contains</c> for a string column. On a string column, a value of exactly <c>*</c> means
    /// "has a value" — not null, and not blank, non-empty after trimming whitespace (#385) — while
    /// <c>*</c> anywhere else stays a literal character to find by <c>contains</c>. On a non-string
    /// column <c>*</c> is not special-cased and is simply an unparseable value, a 400. On a date
    /// column, <c>column:&gt;=value</c> means "on or after" and a null date never matches (#334); on
    /// a string column the <c>&gt;=</c> stays part of the text to find. Each criterion is split on
    /// its *first* colon only, since a value (an ISO-8601 timestamp, for instance) can itself
    /// contain colons.</para>
    ///
    /// <para>Repeats of the same column are legitimate and AND like any other pair:
    /// <c>?filter=Name:air&amp;filter=Name:tur</c> requires both substrings.</para>
    /// </param>
    public sealed record GetBrowsableDataQuery(
        string Table,
        PagedRequest Paging,
        string? Sort = null,
        IReadOnlyList<string>? Filters = null) : IRequest<PagedResult<Dictionary<string, object?>>>
    {
        /// <summary>The column name portion of <see cref="Sort"/>, with a leading <c>-</c> (if any)
        /// stripped. <c>null</c> when <see cref="Sort"/> is null or empty.</summary>
        public string? SortColumn => string.IsNullOrEmpty(Sort) ? null : (Sort[0] == '-' ? Sort[1..] : Sort);

        /// <summary>Whether <see cref="Sort"/> requested descending order (a leading <c>-</c>).</summary>
        public bool SortDescending => !string.IsNullOrEmpty(Sort) && Sort[0] == '-';

        /// <summary>
        /// Each of <see cref="Filters"/> split into its column and value halves on the first
        /// <c>:</c>, in the order given. A criterion that is empty or carries no colon at all is
        /// dropped here — a malformed filter, which
        /// <see cref="GetBrowsableDataQueryValidator"/> rejects with a 400 before this is ever read
        /// for real. Empty when nothing was filtered on.
        /// </summary>
        public IReadOnlyList<(string Column, string Value)> ParsedFilters =>
            (Filters ?? [])
                .Select(filter => (Filter: filter, Separator: filter?.IndexOf(':') ?? -1))
                .Where(x => x.Separator >= 0)
                .Select(x => (Column: x.Filter![..x.Separator], Value: x.Filter[(x.Separator + 1)..]))
                .ToList();
    }
```

Add `using System.Linq;` if the file does not already have it.

- [ ] **Step 4: Make the validator per-criterion**

Replace the two filter rules with rules that iterate. `ForEach` gives one error per offending criterion:

```csharp
            RuleForEach(x => x.Filters)
                .Must(filter => !string.IsNullOrEmpty(filter) && filter.Contains(':'))
                .WithName("filter")
                .WithMessage(filter => $"Filter '{filter}' must be in the form 'column:value'.");

            RuleFor(x => x)
                .Must(BeAllExposedNonSensitiveFilterColumns)
                .WithName("filter")
                .WithMessage(FilterErrorMessage);
```

and replace the two helpers:

```csharp
        private static bool BeAllExposedNonSensitiveFilterColumns(GetBrowsableDataQuery query)
        {
            var table = BrowsableTables.TryGetBySlug(query.Table);
            if (table is null) return true;

            return query.ParsedFilters.All(filter =>
                BrowsableTables.IsColumnExposed(query.Table, filter.Column)
                && !SensitiveProperties.IsSensitive(table.EntityType, filter.Column));
        }

        /// <summary>
        /// One message covering every criterion that failed, each named. With several filters in
        /// play, "Invalid filter column." alone does not say which one to fix — and fixing them one
        /// round-trip at a time is the experience that produces.
        /// </summary>
        private static string FilterErrorMessage(GetBrowsableDataQuery query)
        {
            var table = BrowsableTables.TryGetBySlug(query.Table);
            if (table is null) return "Invalid filter column.";

            var problems = query.ParsedFilters
                .Where(filter => !BrowsableTables.IsColumnExposed(query.Table, filter.Column)
                    || SensitiveProperties.IsSensitive(table.EntityType, filter.Column))
                .Select(filter => SensitiveProperties.IsSensitive(table.EntityType, filter.Column)
                    ? $"Column '{filter.Column}' is sensitive and cannot be filtered on."
                    : $"Unknown filter column '{filter.Column}'. Valid columns: {string.Join(", ", table.Columns)}.")
                .ToList();

            return problems.Count == 0 ? "Invalid filter column." : string.Join(" ", problems);
        }
```

Keep the comment block above the `Must()` helpers explaining why an unresolved table slug is treated as valid here — it is still true, and still the reason these rules never produce a 404.

- [ ] **Step 5: Pass the parsed list straight through in the handler**

```csharp
            var (rows, total) = await _repository.QueryAsync(
                table,
                request.Paging.Page,
                request.Paging.PageSize,
                request.SortColumn,
                request.SortDescending,
                request.ParsedFilters,
                ct);
```

The `var filter = request.ParsedFilter;` line from Task 1 Step 5 goes away.

- [ ] **Step 6: Take a repeated parameter at the endpoint**

In `AdminDataEndpoints.cs`, change the parameter and leave everything else in the lambda alone:

```csharp
            group.MapGet("{table}", async (
                string table,
                [AsParameters] PagedRequest paging,
                string? sort,
                // Repeated, not separated: ?filter=Country:TR&filter=Name:air. Minimal APIs bind a
                // repeated query parameter to an array natively, and a repeated parameter needs no
                // escaping rule — these values are names and free text, where a separator character
                // would be ordinary content (#476).
                string[]? filter,
                IMediator mediator,
                CancellationToken ct) =>
```

The `Send` call needs no edit: `new GetBrowsableDataQuery(table, paging, sort, filter)` now binds `filter` to `Filters`.

- [ ] **Step 7: Write the failing endpoint tests**

In `AdminDataEndpointTests.cs`, next to the existing filter tests:

```csharp
    // ── Several filters, ANDed (#476) ───────────────────────────────────────────

    [Fact]
    public async Task GetTableData_TwoFilters_ReturnsOnlyRowsMatchingBoth()
    {
        var marker = $"{Guid.NewGuid():N}";
        await factory.SeedAsync(db =>
        {
            db.Travelers.AddRange(
                Traveler.Register($"{marker}-a@example.com", $"Ada {marker}"),
                Traveler.Register($"{marker}-b@example.com", $"Bob {marker}"));
        });

        var result = await GetTableDataAsync(
            factory.CreateAdminClient(),
            $"admin/data/travelers?filter=DisplayName:{Uri.EscapeDataString(marker)}&filter=DisplayName:Ada");

        Assert.Equal(1, result.Total);
        Assert.Contains("Ada", Assert.Single(result.Items)[nameof(Traveler.DisplayName)].GetString());
    }

    [Fact]
    public async Task GetTableData_OneOfSeveralFilterColumnsUnknown_Returns400_NamingIt()
    {
        var response = await factory.CreateAdminClient()
            .GetAsync("admin/data/travelers?filter=DisplayName:a&filter=NotARealColumn:x");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("NotARealColumn", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The compatibility guarantee: a single `filter=` is a list of one and behaves exactly as it
    /// did before #476 — the shape every existing panel link and bookmark uses.
    /// </summary>
    [Fact]
    public async Task GetTableData_ASingleFilter_StillBehavesAsItAlwaysHas()
    {
        var marker = $"{Guid.NewGuid():N}";
        await factory.SeedAsync(db => db.Travelers.Add(Traveler.Register($"{marker}@example.com", $"Ada {marker}")));

        var result = await GetTableDataAsync(
            factory.CreateAdminClient(),
            $"admin/data/travelers?filter=DisplayName:{Uri.EscapeDataString(marker)}");

        Assert.Equal(1, result.Total);
    }
```

Match the file's existing seeding helper: if it seeds differently from `factory.SeedAsync(db => …)`, copy the pattern the neighbouring tests use rather than introducing a new one.

- [ ] **Step 8: Run everything and verify it passes**

```bash
dotnet build && dotnet test
```

Expected: PASS, no new warnings. The existing `GetTableData_UnknownFilterColumn_Returns400`, `GetTableData_FilterWithNoColon_IsAMalformedFilter_Returns400`, `GetTableData_NonStringColumnFilteredOnStar_Returns400` and the sensitive-column tests must pass **unmodified** — they are the single-filter compatibility proof.

- [ ] **Step 9: Prove the new tests bite**

Break what you wrote: make `ParsedFilters` return only the first criterion, or make `BeAllExposedNonSensitiveFilterColumns` check only `ParsedFilters.FirstOrDefault()`. Show `GetTableData_TwoFilters_ReturnsOnlyRowsMatchingBoth` and `OneOfSeveralFilterColumnsUnknown_Returns400_NamingIt` failing, then restore.

- [ ] **Step 10: Commit**

```bash
git add FlightAssistant.Application/Queries/Admin/Data/GetBrowsableDataQuery.cs \
        FlightAssistant.API/Endpoints/Admin/AdminDataEndpoints.cs \
        FlightAssistant.Application.Tests/Queries/Admin/Data/GetBrowsableDataQueryValidatorTests.cs \
        FlightAssistant.API.Tests/Integration/AdminDataEndpointTests.cs
git commit -m "feat(data): accept the filter parameter more than once, ANDed (#476)"
```

---

### Task 3: Cap the number of criteria

**Files:**
- Modify: `FlightAssistant.Application/Queries/Admin/Data/GetBrowsableDataQuery.cs`
- Test: `FlightAssistant.Application.Tests/Queries/Admin/Data/GetBrowsableDataQueryValidatorTests.cs`

**Interfaces:**
- Consumes: `GetBrowsableDataQuery.Filters` from Task 2.
- Produces: `GetBrowsableDataQuery.MaxFilters` (`public const int`, value 10).

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void TenCriteria_AreValid()
    {
        var filters = Enumerable.Range(0, GetBrowsableDataQuery.MaxFilters)
            .Select(i => $"{nameof(Traveler.DisplayName)}:{i}")
            .ToArray();

        Assert.True(_validator.Validate(Query(filters)).IsValid);
    }

    [Fact]
    public void MoreThanTenCriteria_AreRejected()
    {
        var filters = Enumerable.Range(0, GetBrowsableDataQuery.MaxFilters + 1)
            .Select(i => $"{nameof(Traveler.DisplayName)}:{i}")
            .ToArray();

        var result = _validator.Validate(Query(filters));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("10"));
    }
```

Add `using System.Linq;` to the test file if needed.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test FlightAssistant.Application.Tests --filter "FullyQualifiedName~GetBrowsableDataQueryValidatorTests"
```

Expected: compile error on `MaxFilters`, then — once it exists — `MoreThanTenCriteria_AreRejected` FAILs because nothing enforces it.

- [ ] **Step 3: Add the constant and the rule**

On the record:

```csharp
        /// <summary>
        /// The most criteria one request may carry. A bound on how much query a single URL can
        /// compose, set far above what the panel's filter form — which adds one criterion at a
        /// time — will ever produce.
        /// </summary>
        public const int MaxFilters = 10;
```

In the validator, before the per-criterion rules:

```csharp
            RuleFor(x => x.Filters)
                .Must(filters => filters is null || filters.Count <= GetBrowsableDataQuery.MaxFilters)
                .WithName("filter")
                .WithMessage($"At most {GetBrowsableDataQuery.MaxFilters} filters may be applied at once.");
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test FlightAssistant.Application.Tests --filter "FullyQualifiedName~GetBrowsableDataQueryValidatorTests"
```

Expected: PASS.

- [ ] **Step 5: Prove the test bites**

Raise `MaxFilters` to 11, or change `<=` to `<`, and show `MoreThanTenCriteria_AreRejected` / `TenCriteria_AreValid` failing respectively. Restore.

- [ ] **Step 6: Commit**

```bash
git add FlightAssistant.Application/Queries/Admin/Data/GetBrowsableDataQuery.cs \
        FlightAssistant.Application.Tests/Queries/Admin/Data/GetBrowsableDataQueryValidatorTests.cs
git commit -m "feat(data): cap a request at ten filters (#476)"
```

---

### Task 4: Document the contract and regenerate the spec

**Files:**
- Modify: `FlightAssistant.API/Endpoints/Admin/AdminDataEndpoints.cs:69-87` (the `.WithDescription(...)` text)
- Modify: `spec/openapi.json` — regenerated, never hand-edited

**Interfaces:**
- Consumes: everything from Tasks 1–3.
- Produces: the published description the panel's generated client carries.

- [ ] **Step 1: Rewrite the filter half of the description**

Replace the sentence beginning "'filter' is 'column:value' — " (keeping everything before and after it intact) with:

```csharp
                "'filter' is 'column:value', and may be given more than once — " +
                "?filter=Country:TR&filter=Name:air — in which case every criterion must match " +
                "(AND). Repeats of the same column AND too, so filter=Name:air&filter=Name:tur " +
                "requires both substrings. At most 10 filters per request; more is a 400. A single " +
                "'filter' behaves exactly as it always has. Per criterion: exact match for a " +
                "non-string column, case-insensitive contains for a string " +
```

so the surrounding text about `*`, `>=` dates and case-sensitive column names continues to read correctly. Re-read the whole string after editing: it is one concatenated sentence sequence and a half-updated clause is worse than the original.

- [ ] **Step 2: Regenerate the OpenAPI spec**

```bash
cd /Users/salihalishov/Repositories/flight-assistant-backend-476
scripts/generate-openapi.sh
git diff --stat spec/openapi.json
```

Expected: a diff confined to `GET /admin/data/{table}` — its `filter` parameter becoming an array of strings, and its description. Anything else moving means **stop and report**.

- [ ] **Step 3: Run the full suite**

```bash
dotnet build && dotnet test
```

Expected: PASS, including `AdminEndpointContractTests`.

- [ ] **Step 4: Commit and open the backend PR**

```bash
git add FlightAssistant.API/Endpoints/Admin/AdminDataEndpoints.cs spec/openapi.json
git commit -m "docs(api): say that filter may be repeated and ANDed (#476)"
git push -u origin feature/479-multi-criteria-filters
gh pr create --base development --title "feat(data): filter a list by more than one criterion (#479)" --body "Closes #479. Spec: \`docs/superpowers/specs/2026-09-19-multi-criteria-filters-design.md\`.

\`filter\` on \`GET /admin/data/{table}\` may now be given more than once, with the criteria ANDed. Each criterion keeps today's grammar and matching rules — \`ApplyFilter\` and \`ConvertFilterValue\` are untouched, only the number of calls changes. A single \`filter=\` is a list of one and behaves exactly as before, pinned by its own test, so every existing link and bookmark keeps working. At most 10 criteria; validation errors name the criterion they are about.

The panel PR follows once this merges.

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

**STOP here and wait for the user.** Phase 2 needs this merged: the panel's regenerated client describes a `filter` the deployed API will not accept until it ships.

---

# Phase 2 — Panel

Runs in the `flight-assistant-admin-panel` repo, in its own worktree, on a branch off `main`, **after the backend PR is merged**.

Open the panel issue first — it does not exist yet, because the backend contract it depends on had to land first:

```bash
cd /Users/salihalishov/Repositories/flight-assistant-admin-panel
gh issue create --title "Filter a list by more than one criterion (panel)" \
  --body "Backend: salih1f1/flight-assistant-backend#479. Spec and plan live in the backend repo (docs/superpowers/specs/2026-09-19-multi-criteria-filters-design.md, docs/superpowers/plans/2026-09-19-multi-criteria-filters.md).

Regenerate the client against the repeatable \`filter\` parameter, carry the grid's criteria as a list in the URL, show applied criteria as removable chips with an Add-filter row beneath, and let the Verified toggle compose with the rest instead of replacing everything."
```

Use that issue's number wherever this phase writes `#<panel issue>`.

### Task 5: Read and write a list of criteria

**Files:**
- Modify: `openapi/openapi.json`, `src/api/generated/**` (synced and regenerated)
- Create: `src/pages/data/appliedFilters.ts`
- Create: `src/pages/data/appliedFilters.test.ts`
- Modify: `src/pages/data/DataBrowserPage.tsx:235` (the `filter` read), `:301-314` (`updateView`), `:345` (`viewKey`), `:241` (the query params)
- Modify: `src/pages/data/DataBrowserPage.test.tsx` (the fake API and existing assertions)

**Interfaces:**
- Consumes: the regenerated `adminGetBrowsableTableDataParams.filter?: string[]`.
- Produces: `parseFilter(filter: string): { column: string; value: string }`, `filterLabel(filter: string): string`, `withFilter(filters: string[], filter: string): string[]`, `withoutFilter(filters: string[], filter: string): string[]`, `withoutColumn(filters: string[], column: string): string[]` — all pure, all exported from `appliedFilters.ts`. Tasks 6 and 7 use them.

- [ ] **Step 1: Sync and regenerate the client**

```bash
cd <panel worktree>
npm run api:sync && npm run api:generate
git diff --stat src/api/generated
```

Expected: `adminGetBrowsableTableDataParams.ts` changes `filter?: string` to `filter?: string[]`, plus the updated description text in `admin.ts`. Never hand-edit these files.

- [ ] **Step 2: Write the failing helper tests**

Create `src/pages/data/appliedFilters.test.ts`:

```ts
import { describe, expect, it } from 'vitest'
import { filterLabel, parseFilter, withFilter, withoutColumn, withoutFilter } from './appliedFilters'

describe('parseFilter', () => {
  it('splits on the first colon only, so a value keeps its own', () => {
    expect(parseFilter('CreatedUtc:2026-09-01T12:30:00Z'))
      .toEqual({ column: 'CreatedUtc', value: '2026-09-01T12:30:00Z' })
  })

  it('reads a criterion with no colon as a column and an empty value', () => {
    expect(parseFilter('DisplayName')).toEqual({ column: 'DisplayName', value: '' })
  })
})

describe('filterLabel', () => {
  it('reads a plain criterion as column and value', () => {
    expect(filterLabel('Country:TR')).toBe('Country: TR')
  })

  it('spells out a ">=" value the way the comparison picker words it', () => {
    expect(filterLabel('CreatedUtc:>=2026-09-01')).toBe('CreatedUtc: on or after 2026-09-01')
  })
})

describe('withFilter', () => {
  it('appends a new criterion', () => {
    expect(withFilter(['Country:TR'], 'Name:air')).toEqual(['Country:TR', 'Name:air'])
  })

  it('keeps a second criterion on the same column: they AND', () => {
    expect(withFilter(['Name:air'], 'Name:tur')).toEqual(['Name:air', 'Name:tur'])
  })

  it('does not add a criterion that is already applied', () => {
    expect(withFilter(['Country:TR'], 'Country:TR')).toEqual(['Country:TR'])
  })
})

describe('withoutFilter', () => {
  it('removes exactly the criterion named, leaving the rest', () => {
    expect(withoutFilter(['Country:TR', 'Name:air'], 'Country:TR')).toEqual(['Name:air'])
  })
})

describe('withoutColumn', () => {
  it('removes every criterion on that column and nothing else', () => {
    expect(withoutColumn(['IsVerified:true', 'Country:TR'], 'IsVerified')).toEqual(['Country:TR'])
  })
})
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
npx vitest run src/pages/data/appliedFilters.test.ts
```

Expected: FAIL — `appliedFilters.ts` does not exist.

- [ ] **Step 4: Write the helpers**

Create `src/pages/data/appliedFilters.ts`:

```ts
/**
 * The applied filter criteria, as the URL carries them: one `filter` search param per criterion,
 * each `column:value`, all ANDed by the API (#476). Kept out of DataBrowserPage so the criteria
 * rules are readable and testable on their own.
 */

/** A criterion split on its *first* colon only — a value (an ISO-8601 timestamp) may contain more. */
export function parseFilter(filter: string): { column: string; value: string } {
  const separator = filter.indexOf(':')
  return separator < 0
    ? { column: filter, value: '' }
    : { column: filter.slice(0, separator), value: filter.slice(separator + 1) }
}

/**
 * A criterion as a chip reads it. A `>=` value is spelled out in the same words the comparison
 * picker uses ("on or after"), rather than shown as an operator nobody typed.
 */
export function filterLabel(filter: string): string {
  const { column, value } = parseFilter(filter)
  return value.startsWith('>=') ? `${column}: on or after ${value.slice(2)}` : `${column}: ${value}`
}

/** Appends a criterion, unless exactly that criterion is already applied. */
export function withFilter(filters: string[], filter: string): string[] {
  return filters.includes(filter) ? filters : [...filters, filter]
}

/** Removes exactly one criterion, leaving every other one applied. */
export function withoutFilter(filters: string[], filter: string): string[] {
  return filters.filter(applied => applied !== filter)
}

/** Removes every criterion on one column — how the Verified toggle drops its own. */
export function withoutColumn(filters: string[], column: string): string[] {
  return filters.filter(applied => parseFilter(applied).column !== column)
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
npx vitest run src/pages/data/appliedFilters.test.ts
```

Expected: PASS.

- [ ] **Step 6: Read and write the list in the page**

In `DataBrowserPage.tsx`:

```tsx
  const filters = searchParams.getAll('filter')
```

replacing `const filter = searchParams.get('filter') ?? undefined`. Pass it to the query:

```tsx
    { Page: page, PageSize: PAGE_SIZE, sort, filter: filters },
```

`updateView` takes the list, writing one entry per criterion:

```tsx
  /** Changes the view in the URL. Any change other than paging starts again at page 1. */
  const updateView = useCallback((changes: { page?: number; sort?: string; filters?: string[] }) => {
    const next = new URLSearchParams(searchParams)
    // page comes only from `changes`, so leaving it out resets to page 1.
    const page = changes.page
    const sort = 'sort' in changes ? changes.sort : searchParams.get('sort') ?? undefined

    if (page === undefined || page === 1) next.delete('page')
    else next.set('page', String(page))

    if (sort === undefined || sort === '') next.delete('sort')
    else next.set('sort', sort)

    // One `filter` entry per criterion: repeated rather than separated, so a value may contain
    // any character at all (#476).
    if (changes.filters) {
      next.delete('filter')
      for (const filter of changes.filters) next.append('filter', filter)
    }

    setSearchParams(next)
  }, [searchParams, setSearchParams])
```

And the selection scope key:

```tsx
  const viewKey = `${table ?? ''}|${page}|${sort ?? ''}|${filters.join('|')}`
```

Update the two existing `updateView({ filter: … })` call sites to the new shape — the `FilterForm`'s `onApply`/`onClear` become `onApply={value => updateView({ filters: withFilter(filters, value) })}` and `onClear={() => updateView({ filters: [] })}`, and the `FilterForm` props change to `filters={filters}` with its `key` becoming `` `${table}|${filters.join('|')}` ``. `FilterForm`'s own props do not change in this task — pass `filter={filters[0]}` so it compiles and behaves exactly as before with one criterion. Task 6 replaces its interface in one go, rather than churning it twice.

- [ ] **Step 7: Teach the test fake API about arrays**

In `DataBrowserPage.test.tsx`, replace the filter block of `dataApi`:

```ts
  const filters = (req.params?.filter ?? []) as string[]
  for (const filter of filters) {
    const [column, ...rest] = filter.split(':')
    const value = rest.join(':')
    if (column === 'Email') throw validationFailed('filter', "Column 'Email' is sensitive and cannot be filtered on.")
    rows = rows.filter(r => String(r[column] ?? '').toLowerCase().includes(value.toLowerCase()))
  }
```

Then update every existing assertion of the shape `filter: 'DisplayName:042'` to `filter: ['DisplayName:042']` — in `DataBrowserPage.test.tsx` and in any other test file that asserts on the data request's params. These updates are **mechanical**: do not change what any existing test is asserting about behaviour.

- [ ] **Step 8: Run the suite**

```bash
npm test
```

Expected: PASS. At this point the page behaves exactly as it did, through a list of one.

- [ ] **Step 9: Prove the new tests bite**

Break a helper you wrote — make `withFilter` replace instead of append, or `parseFilter` split on the last colon — show the matching `appliedFilters.test.ts` case failing, then restore.

- [ ] **Step 10: Commit**

```bash
git add openapi/openapi.json src/api/generated src/pages/data/appliedFilters.ts \
        src/pages/data/appliedFilters.test.ts src/pages/data/DataBrowserPage.tsx \
        src/pages/data/DataBrowserPage.test.tsx
git commit -m "refactor(data): carry the grid's filters as a list (#<panel issue>)"
```

---

### Task 6: Chips, and a form that adds

**Files:**
- Modify: `src/pages/data/DataBrowserPage.tsx:517-527` (the `FilterForm` render) and `:629-712` (`FilterForm` itself)
- Test: `src/pages/data/DataBrowserPage.test.tsx`

**Interfaces:**
- Consumes: `filterLabel`, `withFilter`, `withoutFilter` from `appliedFilters.ts`.
- Produces: the applied-criteria chip list, each chip's remove control labelled `Remove filter {label}`; the form's submit button labelled `Add filter`; the clear control labelled `Clear filters`.

- [ ] **Step 1: Write the failing tests**

In `DataBrowserPage.test.tsx`, replacing the existing `filters on a column and value, then clears the filter` test (its single-criterion behaviour is covered by the new first test here):

```tsx
  it('adds a second criterion instead of replacing the first', async () => {
    const user = userEvent.setup()
    const router = renderAt('/data/travelers')
    await screen.findByText('Traveler 001')

    await user.selectOptions(screen.getByLabelText('Filter column'), 'DisplayName')
    await user.type(screen.getByLabelText('Filter value'), '04')
    await user.click(screen.getByRole('button', { name: 'Add filter' }))
    await screen.findByText('DisplayName: 04')

    await user.selectOptions(screen.getByLabelText('Filter column'), 'DisplayName')
    await user.type(screen.getByLabelText('Filter value'), '2')
    await user.click(screen.getByRole('button', { name: 'Add filter' }))

    await waitFor(() => expect(lastDataRequest()?.params)
      .toEqual({ Page: 1, PageSize: 50, filter: ['DisplayName:04', 'DisplayName:2'] }))
    expect(search(router).getAll('filter')).toEqual(['DisplayName:04', 'DisplayName:2'])
    expect(screen.getByText('Traveler 042')).toBeInTheDocument()
  })

  it('removes one criterion and leaves the others applied', async () => {
    const user = userEvent.setup()
    const router = renderAt('/data/travelers?filter=DisplayName%3A04&filter=DisplayName%3A2')
    await screen.findByText('Traveler 042')

    await user.click(screen.getByRole('button', { name: 'Remove filter DisplayName: 2' }))

    await waitFor(() => expect(search(router).getAll('filter')).toEqual(['DisplayName:04']))
    expect(screen.getByText('DisplayName: 04')).toBeInTheDocument()
    expect(screen.queryByText('DisplayName: 2')).not.toBeInTheDocument()
  })

  it('clears every criterion at once', async () => {
    const user = userEvent.setup()
    const router = renderAt('/data/travelers?filter=DisplayName%3A04&filter=DisplayName%3A2')
    await screen.findByText('Traveler 042')

    await user.click(screen.getByRole('button', { name: 'Clear filters' }))

    expect(await screen.findByText('Page 1 of 3 · 120 total')).toBeInTheDocument()
    expect(search(router).getAll('filter')).toEqual([])
  })

  it('does not add a criterion that is already applied', async () => {
    const user = userEvent.setup()
    const router = renderAt('/data/travelers?filter=DisplayName%3A042')
    await screen.findByText('Traveler 042')

    await user.selectOptions(screen.getByLabelText('Filter column'), 'DisplayName')
    await user.type(screen.getByLabelText('Filter value'), '042')
    await user.click(screen.getByRole('button', { name: 'Add filter' }))

    expect(search(router).getAll('filter')).toEqual(['DisplayName:042'])
    expect(screen.getAllByText('DisplayName: 042')).toHaveLength(1)
  })

  it('shows a date criterion the way its comparison picker words it', async () => {
    renderAt('/data/admin-audit-log?filter=OccurredUtc%3A%3E%3D2026-09-18')
    expect(await screen.findByText('OccurredUtc: on or after 2026-09-18')).toBeInTheDocument()
  })

  it('still filters from a link carrying a single filter', async () => {
    renderAt('/data/travelers?filter=DisplayName%3A042')

    expect(await screen.findByText('Traveler 042')).toBeInTheDocument()
    expect(screen.getByText('DisplayName: 042')).toBeInTheDocument()
    expect(lastDataRequest()?.params).toEqual({ Page: 1, PageSize: 50, filter: ['DisplayName:042'] })
  })
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
npx vitest run src/pages/data/DataBrowserPage.test.tsx
```

Expected: FAIL — there is no `Add filter` button, no chips, and no `Clear filters`.

- [ ] **Step 3: Render the chips**

In `DataBrowserPage.tsx`, above the `<FilterForm .../>`:

```tsx
          {filters.length > 0 && (
            <div className="flex flex-wrap items-center gap-2">
              {filters.map(applied => (
                <span
                  key={applied}
                  className="inline-flex items-center gap-1 rounded-full bg-gray-100 py-1 pl-3 pr-1 text-sm text-gray-700"
                >
                  {filterLabel(applied)}
                  <button
                    type="button"
                    aria-label={`Remove filter ${filterLabel(applied)}`}
                    onClick={() => updateView({ filters: withoutFilter(filters, applied) })}
                    className="rounded-full p-1 text-gray-500 hover:bg-gray-200 hover:text-gray-900"
                  >
                    <X className="h-3 w-3" />
                  </button>
                </span>
              ))}
              <Button type="button" variant="ghost" size="sm" onClick={() => updateView({ filters: [] })}>
                Clear filters
              </Button>
            </div>
          )}
```

Import `X` from `lucide-react` alongside the file's existing icon imports, and `filterLabel`, `withFilter`, `withoutFilter` from `./appliedFilters`.

- [ ] **Step 4: Make the form add rather than replace**

`FilterForm`'s props and its applied-criterion reading change — it no longer shows what is applied, since the chips do that; it is purely the "add one" row:

```tsx
function FilterForm({ columns, published, onAdd }: {
  columns: string[]
  published: BrowsableColumnDto[] | null | undefined
  onAdd: (filter: string) => void
}) {
  const [column, setColumn] = useState('')
  const [value, setValue] = useState('')
  // ">=" is the API's only operator, and dates its only operand (#334). A fresh date filter leads
  // with "on or after", since an exact match on a stored timestamp almost never matches the day an
  // admin picks.
  const [comparison, setComparison] = useState('gte')

  const options = filterColumns(published, columns, undefined)
```

The `chosen` / `chosenColumn` / `control` / `picked` / `effective` / `submitted` lines stay exactly as they are. The submit handler adds and then resets the row:

```tsx
      onSubmit={e => {
        e.preventDefault()
        if (!chosen || !effective || control === 'none') return
        onAdd(`${chosen}:${submitted}`)
        setValue('')
        setComparison('gte')
      }}
```

The **value** and the comparison reset; the **column stays selected**. The spec says "resets to a fresh column pick", which was written from how the old form remounted on apply — but with criteria ANDing, adding two criteria on the same column (`Name:air` then `Name:tur`) is an ordinary thing to do, and re-picking the column every time is friction for no gain. Note the deviation in the PR description.

The button reads `Add filter`, and the form's own `Clear filter` button goes away — the chip row owns clearing now:

```tsx
      <Button type="submit" variant="outline" disabled={control === 'none'}>Add filter</Button>
```

At the render site, the props and key become:

```tsx
          <FilterForm
            // Remount on a table change so the inputs never carry another table's columns.
            key={table}
            // Every column, hidden or not: filtering on one doesn't need it on screen.
            columns={columnNames}
            // What GET /admin/data/tables published about this table's columns (#451), so the value
            // control can match the column's kind. Undefined against an API that publishes none.
            published={tables.data?.find(t => t.slug === table)?.columns}
            onAdd={value => updateView({ filters: withFilter(filters, value) })}
          />
```

Delete the now-unused `filter`/`onApply`/`onClear` props and the `appliedColumn`/`appliedValue` parsing inside the form.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
npm test
```

Expected: PASS. Two existing tests need their *expectations* updated in this step, and only these two: `keeps whichever comparison the applied filter carries` (the form no longer reflects an applied criterion — the chip does; assert the chip's text and delete the input-value assertion) and any test clicking `Apply filter`, which is now `Add filter`. If a third test needs changing, stop: something in Step 4 went further than intended.

- [ ] **Step 6: Prove the new tests bite**

Break what you wrote — make the chip's × call `updateView({ filters: [] })`, or make `onAdd` pass `[value]` instead of `withFilter(filters, value)` — show `removes one criterion and leaves the others applied` and `adds a second criterion instead of replacing the first` failing, then restore.

- [ ] **Step 7: Commit**

```bash
git add src/pages/data/DataBrowserPage.tsx src/pages/data/DataBrowserPage.test.tsx
git commit -m "feat(data): show applied filters as chips and add rather than replace (#<panel issue>)"
```

---

### Task 7: The Verified toggle joins the criteria

**Files:**
- Modify: `src/pages/data/DataBrowserPage.tsx:72-76` (`VERIFIED_FILTERS`) and `:488-502` (the toggle)
- Test: `src/pages/data/VerifyReferenceData.test.tsx`

**Interfaces:**
- Consumes: `withFilter`, `withoutColumn`, `parseFilter` from `appliedFilters.ts`.
- Produces: no new exports. The toggle's three buttons keep their labels (`All`, `Verified`, `Unverified`) and their `aria-pressed` semantics.

- [ ] **Step 1: Write the failing tests**

In `VerifyReferenceData.test.tsx`, beside the existing toggle test:

```tsx
  it('keeps a column filter when the Verified toggle is used', async () => {
    const user = userEvent.setup()
    const router = renderAt('/data/airports?filter=Name%3Aist')
    await screen.findByText('Istanbul')

    await user.click(screen.getByRole('button', { name: 'Unverified' }))

    await waitFor(() => expect(new URLSearchParams(router.state.location.search).getAll('filter'))
      .toEqual(['Name:ist', 'IsVerified:false']))
  })

  it('All removes only the verified criterion', async () => {
    const user = userEvent.setup()
    const router = renderAt('/data/airports?filter=Name%3Aist&filter=IsVerified%3Afalse')
    await screen.findByText('Istanbul')

    await user.click(screen.getByRole('button', { name: 'All' }))

    await waitFor(() => expect(new URLSearchParams(router.state.location.search).getAll('filter'))
      .toEqual(['Name:ist']))
  })

  it('marks the toggle from the criterion that is applied, whatever else is', async () => {
    renderAt('/data/airports?filter=Name%3Aist&filter=IsVerified%3Atrue')
    await screen.findByText('Istanbul')

    expect(screen.getByRole('button', { name: 'Verified' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('button', { name: 'All' })).toHaveAttribute('aria-pressed', 'false')
  })
```

Use whatever `renderAt` / seeded rows that file already provides; if its helper takes no query string, copy the pattern from `DataBrowserPage.test.tsx`'s `renderAt` rather than inventing a third.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
npx vitest run src/pages/data/VerifyReferenceData.test.tsx
```

Expected: FAIL — the toggle replaces the whole filter list.

- [ ] **Step 3: Make the toggle compose**

`VERIFIED_FILTERS` keeps its shape; only the handlers and the pressed state change:

```tsx
            {verifier && (
              // One criterion among the rest: pressing these adds or removes IsVerified and leaves
              // every other filter applied, so "unverified airports in Turkey" is expressible (#476).
              <div role="group" aria-label="Verified filter" className="inline-flex overflow-hidden rounded-md border border-gray-200">
                {VERIFIED_FILTERS.map(option => {
                  const pressed = option.filter
                    ? filters.includes(option.filter)
                    : !filters.some(applied => parseFilter(applied).column === VERIFIED_COLUMN)
                  return (
                    <button
                      key={option.label}
                      type="button"
                      aria-pressed={pressed}
                      onClick={() => updateView({
                        filters: option.filter
                          ? withFilter(withoutColumn(filters, VERIFIED_COLUMN), option.filter)
                          : withoutColumn(filters, VERIFIED_COLUMN),
                      })}
                      className={cn('px-3 py-1.5 text-sm', pressed ? 'bg-gray-900 text-white' : 'bg-white text-gray-600 hover:bg-gray-50')}
                    >
                      {option.label}
                    </button>
                  )
                })}
              </div>
            )}
```

with, beside `VERIFIED_FILTERS`:

```tsx
/** The column the Verified toggle owns: pressing one of its buttons replaces only this column's criterion. */
const VERIFIED_COLUMN = 'IsVerified'
```

`withoutColumn` before `withFilter` is what makes Verified → Unverified a swap rather than a contradiction that matches nothing.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
npm test
```

Expected: PASS, including the existing `VerifyReferenceData.test.tsx` toggle test (whose single-filter assertion still holds — with nothing else applied, pressing Verified still produces exactly `IsVerified:true`).

- [ ] **Step 5: Prove the new tests bite**

Break what you wrote — drop the `withoutColumn` wrapper (pressing Verified then Unverified leaves both criteria and matches nothing), or compute `pressed` by comparing the whole list to one criterion — and show the matching test failing before restoring.

- [ ] **Step 6: Lint, build, full suite**

```bash
npm run lint && npm run build && npm test
```

Expected: all three clean.

- [ ] **Step 7: Commit and open the panel PR**

```bash
git add src/pages/data/DataBrowserPage.tsx src/pages/data/VerifyReferenceData.test.tsx
git commit -m "feat(data): let the Verified toggle compose with the other filters (#<panel issue>)"
git push -u origin feature/<panel issue>-multi-criteria-filters
gh pr create --title "feat(data): filter a list by more than one criterion (#<panel issue>)" --body "Closes #<panel issue>. Backend: salih1f1/flight-assistant-backend#479 (merged). Spec and plan live in the backend repo.

Applied criteria now show as removable chips above the filter row, whose button adds a criterion instead of replacing the one applied. The Verified toggle became one criterion among the rest, so \"unverified airports in Turkey\" is expressible. The URL carries one \`filter\` entry per criterion, so a filtered view is still shareable — and a link carrying a single \`filter\` (every dashboard tile, the pending-queue link, the detail-page links) still works untouched.

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

**STOP and wait for the user.**

---

## Verification

Before claiming the work is done, with both PRs open:

- `dotnet build && dotnet test` in the backend worktree — clean, no new warnings.
- `git diff --stat spec/openapi.json` — confined to `GET /admin/data/{table}`.
- `npm run lint && npm run build && npm test` in the panel worktree — all clean.
- Every test added in Tasks 1–3 and 5–7 has been shown failing against a break derived from the code as written, and restored.
- The six existing single-filter link builders are untouched: `git diff --stat` in the panel names none of `StatsSection.tsx`, `NeedsAttentionSection.tsx`, `pendingVerifications.ts`, `TravelerDetailPage.tsx`, `FlightDetailPage.tsx`, `HealthReport.tsx`.
