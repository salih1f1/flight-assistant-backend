using FlightAssistant.Application.Administration;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

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
    private static readonly BrowsableTable AdminAuditLogTable = BrowsableTables.TryGetBySlug("admin-audit-log")!;
    private static readonly BrowsableTable LoungeProviderLinksTable = BrowsableTables.TryGetBySlug("lounge-provider-links")!;

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

        // I2: the primary-key tiebreaker (see ApplySort) must show up as a real ORDER BY clause in
        // the query the production path actually composes — not just "some test happens to pass on
        // 6 sequentially-scanned rows regardless of whether it's there" (finding 2 of the #315
        // final review: deleting the tiebreaker left every BrowsableDataRepositoryTests test green
        // because Postgres seq-scans a tiny freshly-reset table in physical/insertion order either
        // way). Asserting on the SQL text itself is the only check that fails when the tiebreaker
        // is removed, regardless of row count or scan plan.
        Assert.Contains("ORDER BY t.\"Id\"", sql, StringComparison.Ordinal);
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

    // ── Unparseable filter values are a 400, never a 500 (C1) ──────────────────

    /// <summary>
    /// C1 (#315 final review, finding 1): <c>Enum.Parse</c> throws <see cref="ArgumentException"/>
    /// for a name it doesn't recognise — not <see cref="FormatException"/> — so it escaped
    /// <c>ConvertFilterValue</c>'s original catch filter uncaught, and <c>GlobalExceptionHandler</c>
    /// mapped the unhandled exception to a 500. Reproduced end-to-end by the reviewer against
    /// <c>GET /admin/data/admin-audit-log?filter=Action:NotARealAction</c>.
    ///
    /// <para>
    /// Goes through <see cref="IBrowsableDataRepository.QueryAsync"/> — the method the handler
    /// actually calls — rather than the test-only <c>BuildQueryString</c> seam used elsewhere in
    /// this file. <c>BuildQueryString</c>'s target method is synchronous, so reflection's
    /// <c>MethodInfo.Invoke</c> wraps whatever it throws in <see cref="System.Reflection.TargetInvocationException"/>
    /// — an artifact of that seam, not what production sees. <c>QueryGenericAsync</c> is
    /// <c>async</c>, so the exception is captured into the returned <see cref="Task"/> instead of
    /// thrown synchronously through <c>Invoke</c>, and <c>await</c> unwraps it to the original,
    /// unwrapped <see cref="DomainException"/> — exactly what reaches <c>GlobalExceptionHandler</c>.
    /// No seeded rows needed either way: <c>ConvertFilterValue</c> runs eagerly while
    /// <c>ApplyFilter</c> builds the expression tree, before any query executes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task QueryAsync_UnparseableEnumFilterValue_ThrowsDomainException400_NotAnUnhandledArgumentException()
    {
        await using var db = CreateDbContext();

        var ex = await Assert.ThrowsAsync<DomainException>(() => Repository(db).QueryAsync(
            AdminAuditLogTable, page: 1, pageSize: 50, sortColumn: null, sortDescending: false,
            filterColumn: nameof(AdminAuditLog.Action), filterValue: "NotARealAction"));

        Assert.Equal(400, ex.StatusCode);
    }

    /// <summary>An empty filter value (<c>?filter=Action:</c>, the second of the reviewer's two
    /// reproductions) hits the same <see cref="ArgumentException"/> path as an unrecognised name —
    /// <c>Enum.Parse</c> throws for an empty/whitespace-only string too.</summary>
    [Fact]
    public async Task QueryAsync_EmptyEnumFilterValue_ThrowsDomainException400_NotAnUnhandledArgumentException()
    {
        await using var db = CreateDbContext();

        var ex = await Assert.ThrowsAsync<DomainException>(() => Repository(db).QueryAsync(
            AdminAuditLogTable, page: 1, pageSize: 50, sortColumn: null, sortDescending: false,
            filterColumn: nameof(AdminAuditLog.Action), filterValue: ""));

        Assert.Equal(400, ex.StatusCode);
    }

    /// <summary>The fix is a catch-filter addition, not a special case for one column — pinned
    /// against the *other* enum flavour in the registry (<see cref="LoungeProviderLink.Role"/>,
    /// <c>.HasConversion&lt;int&gt;()</c>, versus <see cref="AdminAuditLog.Action"/>'s
    /// <c>.HasConversion&lt;string&gt;()</c> above) so the shape of the fix is proven, not just the
    /// one reported case. Mirrors the reviewer's second reproduction,
    /// <c>GET /admin/data/lounge-provider-links?filter=Role:NotARole</c>.</summary>
    [Fact]
    public async Task QueryAsync_UnparseableEnumFilterValue_IntBackedEnum_ThrowsDomainException400()
    {
        await using var db = CreateDbContext();

        var ex = await Assert.ThrowsAsync<DomainException>(() => Repository(db).QueryAsync(
            LoungeProviderLinksTable, page: 1, pageSize: 50, sortColumn: null, sortDescending: false,
            filterColumn: nameof(LoungeProviderLink.Role), filterValue: "NotARole"));

        Assert.Equal(400, ex.StatusCode);
    }

    /// <summary>
    /// Non-enum branches of <c>ConvertFilterValue</c> already threw the correct 400 before this fix
    /// (they throw <see cref="FormatException"/>, which was already in the catch filter) — but had
    /// no unit test pinning that behaviour at all; the reviewer's evidence was an end-to-end probe
    /// against the live endpoint, not a test in this suite. Covers a <see cref="Guid"/> column so
    /// adding <see cref="ArgumentException"/> to the catch filter is proven not to have changed
    /// this already-correct branch.
    /// </summary>
    [Fact]
    public async Task QueryAsync_UnparseableGuidFilterValue_ThrowsDomainException400()
    {
        await using var db = CreateDbContext();

        var ex = await Assert.ThrowsAsync<DomainException>(() => Repository(db).QueryAsync(
            TravelersTable, page: 1, pageSize: 50, sortColumn: null, sortDescending: false,
            filterColumn: nameof(Traveler.Id), filterValue: "not-a-guid"));

        Assert.Equal(400, ex.StatusCode);
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
    /// a tie of <see cref="TiedRowCount"/> rows on the requested sort column, paged with
    /// <see cref="TiedRowPageSize"/>, must yield disjoint, exhaustive pages whose concatenated
    /// order exactly matches primary-key order — not merely "every row appeared exactly once,"
    /// which a stable-but-arbitrary order (e.g. physical scan order) would also satisfy. Without
    /// the primary-key tiebreaker every ordering always appends, PostgreSQL does not guarantee
    /// stable ordering across separate query executions for a tie on the sort column alone — a row
    /// could appear on two pages, or on none.
    ///
    /// <para>
    /// I2 (#315 final review, finding 2): the original version of this test only asserted
    /// <c>seen.Distinct().Count() == 6</c> across 6 rows, which stayed green even with the
    /// tiebreaker deleted outright — on 6 rows in a freshly-reset table Postgres seq-scans in
    /// physical (insertion) order and returns that same order on every execution regardless of
    /// whether an <c>ORDER BY</c> exists, so the assertion held by coincidence, not by proof. Row
    /// count alone can't fix that: a static table with no concurrent writes gives a *repeatable*
    /// scan order either way, so no amount of seeding makes "does every row appear once" fail
    /// under the mutation. What actually discriminates is asserting the *sequence* against a
    /// ground truth fetched independently via Postgres's own <c>ORDER BY "Id"</c> (not via .NET's
    /// in-memory <see cref="Guid"/> ordering, which is a different, irrelevant question) — under
    /// the tiebreaker the two must be identical since every row ties on <c>Platform</c>; under the
    /// mutation the returned sequence is physical/insertion order, unrelated to the random-GUID
    /// primary-key order, so the two sequences diverge with overwhelming probability.
    /// </para>
    /// </summary>
    [Fact]
    public async Task QueryAsync_RowsSharingOneSortValue_PageDeterministically_ViaPrimaryKeyTiebreaker()
    {
        await using var db = CreateDbContext();
        var traveler = NewTraveler();
        for (var i = 0; i < TiedRowCount; i++)
            traveler.AddDeviceToken("ios", $"token-{Guid.NewGuid():N}", DateTime.UtcNow);
        db.Travelers.Add(traveler);
        await db.SaveChangesAsync();

        var expectedOrder = await ExpectedIdOrderAsync(db, traveler.Id);

        var seen = new List<Guid>();
        for (var page = 1; page <= TiedRowCount / TiedRowPageSize; page++)
        {
            var (items, total) = await Repository(db).QueryAsync(
                DeviceTokensTable, page, TiedRowPageSize,
                sortColumn: nameof(DeviceToken.Platform), sortDescending: false,
                filterColumn: null, filterValue: null);

            Assert.Equal(TiedRowCount, total);
            seen.AddRange(items.Select(i => (Guid)i[nameof(DeviceToken.Id)]!));
        }

        Assert.Equal(expectedOrder, seen);
    }

    [Fact]
    public async Task QueryAsync_NoExplicitSort_IsStillStableAcrossPages_ViaThePrimaryKeyAlone()
    {
        await using var db = CreateDbContext();
        var traveler = NewTraveler();
        for (var i = 0; i < TiedRowCount; i++)
            traveler.AddDeviceToken("ios", $"token-{Guid.NewGuid():N}", DateTime.UtcNow);
        db.Travelers.Add(traveler);
        await db.SaveChangesAsync();

        var expectedOrder = await ExpectedIdOrderAsync(db, traveler.Id);

        var seen = new List<Guid>();
        for (var page = 1; page <= TiedRowCount / TiedRowPageSize; page++)
        {
            var (items, total) = await Repository(db).QueryAsync(
                DeviceTokensTable, page, TiedRowPageSize, sortColumn: null, sortDescending: false,
                filterColumn: null, filterValue: null);

            Assert.Equal(TiedRowCount, total);
            seen.AddRange(items.Select(i => (Guid)i[nameof(DeviceToken.Id)]!));
        }

        Assert.Equal(expectedOrder, seen);
    }

    // Large enough that these tests exercise more than a single page-size's worth of ties (I2:
    // "seed enough tied rows that ordering actually matters"); the real discriminator is the
    // exact-sequence assertion against ExpectedIdOrderAsync below, which holds regardless of N —
    // see the class doc on QueryAsync_RowsSharingOneSortValue_PageDeterministically_ViaPrimaryKeyTiebreaker
    // for why row count alone was never going to be sufficient on its own.
    private const int TiedRowCount = 24;
    private const int TiedRowPageSize = 6;

    /// <summary>Ground truth for "what order does the primary-key tiebreaker produce", fetched via
    /// Postgres's own <c>ORDER BY "Id"</c> rather than .NET's in-memory <see cref="Guid"/> ordering
    /// — the two are a different question, and only the former is what the production code, or a
    /// test standing in for Postgres's behaviour, can rely on.</summary>
    private static Task<List<Guid>> ExpectedIdOrderAsync(FlightAssistantDbContext db, Guid travelerId) =>
        db.Set<DeviceToken>()
            .Where(d => d.TravelerId == travelerId)
            .OrderBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync();

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

    // ── GetRowAsync (#318 — the audited reveal's read half) ─────────────────────

    /// <summary>
    /// Unlike every <c>QueryAsync</c> test above, this is the one read path in
    /// <see cref="BrowsableDataRepository"/> that must hand back the real, unmasked value —
    /// masking is applied by the reveal handler's caller-side allowlist check, never by this
    /// method. Proven against real Postgres so a future change that routed this through the same
    /// masking helper <c>GetBrowsableDataQueryHandler</c> uses would be caught here, not just in a
    /// mocked handler test.
    /// </summary>
    [Fact]
    public async Task GetRowAsync_ExistingId_ReturnsTheRawUnmaskedValue()
    {
        await using var db = CreateDbContext();
        var traveler = NewTraveler("Findable Traveler");
        db.Travelers.Add(traveler);
        await db.SaveChangesAsync();

        var row = await Repository(db).GetRowAsync(TravelersTable, traveler.Id.ToString());

        Assert.NotNull(row);
        Assert.Equal(traveler.Email, row![nameof(Traveler.Email)]);
        Assert.NotEqual("<redacted>", row[nameof(Traveler.Email)]);
    }

    [Fact]
    public async Task GetRowAsync_UnknownId_ReturnsNull()
    {
        await using var db = CreateDbContext();

        var row = await Repository(db).GetRowAsync(TravelersTable, Guid.NewGuid().ToString());

        Assert.Null(row);
    }

    /// <summary>An id that doesn't even parse as the key column's CLR type (a Guid here) is "no
    /// such row", not a thrown exception — an id lookup should never turn a mistyped id into a
    /// 500.</summary>
    [Fact]
    public async Task GetRowAsync_IdThatDoesNotParseAsTheKeyType_ReturnsNull_NotAnException()
    {
        await using var db = CreateDbContext();
        db.Travelers.Add(NewTraveler());
        await db.SaveChangesAsync();

        var row = await Repository(db).GetRowAsync(TravelersTable, "not-a-guid");

        Assert.Null(row);
    }

    [Fact]
    public async Task GetRowAsync_DeviceToken_ReturnsTheRawUnmaskedToken()
    {
        await using var db = CreateDbContext();
        var traveler = NewTraveler();
        var rawToken = $"very-secret-token-{Guid.NewGuid():N}";
        var deviceToken = traveler.AddDeviceToken("ios", rawToken);
        db.Travelers.Add(traveler);
        await db.SaveChangesAsync();

        var row = await Repository(db).GetRowAsync(DeviceTokensTable, deviceToken.Id.ToString());

        Assert.NotNull(row);
        Assert.Equal(rawToken, row![nameof(DeviceToken.Token)]);
    }
}
