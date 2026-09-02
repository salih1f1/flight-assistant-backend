using FlightAssistant.Application.Administration;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// <see cref="BrowsableDataRepository"/> against real PostgreSQL (#315 Task 2).
///
/// <para>
/// <b>Why this class exists at all.</b> The dynamic query path — <c>EF.Property&lt;TProperty&gt;</c>
/// composition, the case-insensitive string filter, the primary-key sort tiebreaker, and any
/// timestamp comparison — is exactly what <c>UseInMemoryDatabase</c> models differently: InMemory
/// evaluates most LINQ client-side rather than translating it to SQL, and (per the C1 regression
/// this repo has already hit once, in <c>AdminAuditRepositoryTests</c>) it ignores
/// <see cref="DateTimeKind"/> entirely, so an <see cref="ArgumentException"/> from writing an
/// <c>Unspecified</c>-kind value against a <c>timestamptz</c> column would never surface there.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class BrowsableDataRepositoryTests : PostgresTestBase
{
    public BrowsableDataRepositoryTests(PostgresContainerFixture fixture) : base(fixture) { }

    private static BrowsableDataRepository Repository(FlightAssistantDbContext db) => new(db);

    private static readonly BrowsableTable TravelersTable = BrowsableTables.TryGetBySlug("travelers")!;
    private static readonly BrowsableTable DeviceTokensTable = BrowsableTables.TryGetBySlug("device-tokens")!;

    private static Traveler NewTraveler(string? displayName = null) =>
        Traveler.Register($"{Guid.NewGuid():N}@example.com", displayName);

    // ── Case-insensitive string filter ──────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_StringFilter_IsCaseInsensitiveContains_AgainstRealPostgres()
    {
        await using var db = CreateDbContext();
        db.Travelers.AddRange(
            NewTraveler("Ada Lovelace"),
            NewTraveler("Bob Smith"));
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).QueryAsync(
            TravelersTable, page: 1, pageSize: 50,
            sortColumn: null, sortDescending: false,
            filterColumn: nameof(Traveler.DisplayName), filterValue: "ADA love");

        Assert.Equal(1, total);
        Assert.Equal("Ada Lovelace", Assert.Single(items)[nameof(Traveler.DisplayName)]);
    }

    /// <summary>
    /// The brief's explicit requirement: the case-insensitive comparison must genuinely translate
    /// to SQL rather than falling back to pulling the table into memory and evaluating
    /// <see cref="string.ToLower()"/> client-side — the usual way this pattern silently regresses.
    /// Proven against the exact query <see cref="BrowsableDataRepository.QueryAsync"/> composes
    /// (via the internal <c>BuildQueryString</c> seam), not a hand-rebuilt stand-in.
    /// </summary>
    [Fact]
    public void ApplyFilter_StringContains_TranslatesToSql()
    {
        using var db = CreateDbContext();

        var sql = Repository(db).BuildQueryString(
            TravelersTable, sortColumn: null, sortDescending: false,
            filterColumn: nameof(Traveler.DisplayName), filterValue: "ann");

        // A client-evaluated fallback would produce a query with no WHERE clause referencing the
        // column at all (EF Core would instead emit a client-eval warning and filter in memory
        // after pulling every row). Asserting on the generated SQL text itself is the only way to
        // tell the two apart — passing output alone proves nothing.
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DisplayName", sql, StringComparison.Ordinal);
        Assert.True(
            sql.Contains("lower(", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase),
            $"Expected the generated SQL to case-fold in the database (lower()/ILIKE), got:\n{sql}");
    }

    [Fact]
    public async Task QueryAsync_StringFilter_ANullColumnIsNeverAMatch()
    {
        await using var db = CreateDbContext();
        db.Travelers.AddRange(
            NewTraveler(displayName: null),
            NewTraveler("Has a name"));
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).QueryAsync(
            TravelersTable, 1, 50, null, false, nameof(Traveler.DisplayName), "name");

        Assert.Equal(1, total);
        Assert.Equal("Has a name", Assert.Single(items)[nameof(Traveler.DisplayName)]);
    }

    // ── Exact match on a non-string column ──────────────────────────────────────

    [Fact]
    public async Task QueryAsync_NonStringFilter_IsAnExactMatch()
    {
        await using var db = CreateDbContext();
        var traveler = NewTraveler("Findable");
        db.Travelers.AddRange(traveler, NewTraveler("Not findable"));
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).QueryAsync(
            TravelersTable, 1, 50, null, false, nameof(Traveler.Id), traveler.Id.ToString());

        Assert.Equal(1, total);
        Assert.Equal("Findable", Assert.Single(items)[nameof(Traveler.DisplayName)]);
    }

    // ── Timestamp filter safety (the #313 C1 bug class, one layer down) ────────

    /// <summary>
    /// <c>DeviceToken.LastSeenUtc</c> is mapped <c>timestamptz</c>. A raw filter value with no
    /// offset (e.g. <c>2026-09-01T12:00:00</c>, exactly what a hand-typed admin-panel filter would
    /// look like) must not throw the C1 <see cref="ArgumentException"/> — <see cref="BrowsableDataRepository"/>
    /// parses it as <see cref="DateTimeOffset"/> and projects through <c>.UtcDateTime</c> rather
    /// than calling <see cref="DateTime.Parse(string)"/> directly, exactly as <c>AdminAuditEndpoints</c>
    /// does at the API boundary for the same reason.
    /// </summary>
    [Fact]
    public async Task QueryAsync_TimestampFilter_WithNoOffsetInTheRawValue_DoesNotThrow_AndStillMatches()
    {
        await using var db = CreateDbContext();
        var traveler = NewTraveler();
        var seenUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        traveler.AddDeviceToken("ios", $"token-{Guid.NewGuid():N}", seenUtc);
        db.Travelers.Add(traveler);
        await db.SaveChangesAsync();

        (IReadOnlyList<IReadOnlyDictionary<string, object?>> Items, int Total) result = (Array.Empty<IReadOnlyDictionary<string, object?>>(), -1);
        var exception = await Record.ExceptionAsync(async () =>
            result = await Repository(db).QueryAsync(
                DeviceTokensTable, 1, 50, null, false,
                nameof(FlightAssistant.Domain.Travelers.Entities.DeviceToken.LastSeenUtc), "2026-09-01T12:00:00"));

        Assert.Null(exception);
        Assert.Equal(1, result.Total);
    }

    // ── Total is the filtered count ─────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_Total_IsTheFilteredCount_NotTheTableCount()
    {
        await using var db = CreateDbContext();
        var matching = Enumerable.Range(1, 5).Select(_ => NewTraveler("Match Me")).ToArray();
        var excluded = Enumerable.Range(1, 3).Select(_ => NewTraveler("Skip Me")).ToArray();
        db.Travelers.AddRange(matching.Concat(excluded));
        await db.SaveChangesAsync();

        var (items, total) = await Repository(db).QueryAsync(
            TravelersTable, page: 1, pageSize: 2, sortColumn: nameof(Traveler.DisplayName), sortDescending: false,
            filterColumn: nameof(Traveler.DisplayName), filterValue: "Match Me");

        Assert.Equal(5, total);
        Assert.Equal(2, items.Count);
    }

    // ── Stable paging via the primary-key tiebreaker ────────────────────────────

    /// <summary>
    /// Mirrors <c>AdminAuditRepositoryTests.SearchAsync_RowsSharingOneActionUtc_PageDeterministically_ViaIdTiebreaker</c>:
    /// a tie of 6 rows on the requested sort column, paged with <c>pageSize=2</c>, must yield three
    /// disjoint, exhaustive pages. Without the primary-key tiebreaker every ordering always
    /// appends, PostgreSQL does not guarantee stable ordering across separate query executions for
    /// a tie on the sort column alone — a row could appear on two pages, or on none.
    /// </summary>
    [Fact]
    public async Task QueryAsync_RowsSharingOneSortValue_PageDeterministically_ViaPrimaryKeyTiebreaker()
    {
        await using var db = CreateDbContext();
        var traveler = NewTraveler();
        for (var i = 0; i < 6; i++)
            traveler.AddDeviceToken("ios", $"token-{Guid.NewGuid():N}", DateTime.UtcNow);
        db.Travelers.Add(traveler);
        await db.SaveChangesAsync();

        var seen = new List<object?>();
        for (var page = 1; page <= 3; page++)
        {
            var (items, total) = await Repository(db).QueryAsync(
                DeviceTokensTable, page, pageSize: 2,
                sortColumn: nameof(FlightAssistant.Domain.Travelers.Entities.DeviceToken.Platform), sortDescending: false,
                filterColumn: null, filterValue: null);

            Assert.Equal(6, total);
            seen.AddRange(items.Select(i => i[nameof(FlightAssistant.Domain.Travelers.Entities.DeviceToken.Id)]));
        }

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }

    [Fact]
    public async Task QueryAsync_NoExplicitSort_IsStillStableAcrossPages_ViaThePrimaryKeyAlone()
    {
        await using var db = CreateDbContext();
        var traveler = NewTraveler();
        for (var i = 0; i < 6; i++)
            traveler.AddDeviceToken("ios", $"token-{Guid.NewGuid():N}", DateTime.UtcNow);
        db.Travelers.Add(traveler);
        await db.SaveChangesAsync();

        var seen = new List<object?>();
        for (var page = 1; page <= 3; page++)
        {
            var (items, total) = await Repository(db).QueryAsync(
                DeviceTokensTable, page, pageSize: 2, sortColumn: null, sortDescending: false,
                filterColumn: null, filterValue: null);

            Assert.Equal(6, total);
            seen.AddRange(items.Select(i => i[nameof(FlightAssistant.Domain.Travelers.Entities.DeviceToken.Id)]));
        }

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }

    // ── Sort direction ───────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_SortDescending_ReversesOrder()
    {
        await using var db = CreateDbContext();
        db.Travelers.AddRange(NewTraveler("Alpha"), NewTraveler("Charlie"), NewTraveler("Bravo"));
        await db.SaveChangesAsync();

        var (items, _) = await Repository(db).QueryAsync(
            TravelersTable, 1, 50, sortColumn: nameof(Traveler.DisplayName), sortDescending: true,
            filterColumn: null, filterValue: null);

        Assert.Equal(["Charlie", "Bravo", "Alpha"], items.Select(i => i[nameof(Traveler.DisplayName)]));
    }

    // ── Row counts ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CountAsync_ReflectsRowsSeededSinceReset()
    {
        await using var db = CreateDbContext();
        db.Travelers.AddRange(NewTraveler(), NewTraveler(), NewTraveler());
        await db.SaveChangesAsync();

        var count = await Repository(db).CountAsync(TravelersTable);

        Assert.Equal(3, count);
    }
}
