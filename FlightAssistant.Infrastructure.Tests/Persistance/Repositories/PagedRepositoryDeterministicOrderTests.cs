using System.Data.Common;
using System.Text.RegularExpressions;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// Every paged repository read must order by something unique before it pages (#328).
///
/// <para>
/// PostgreSQL guarantees no row order beyond what <c>ORDER BY</c> pins, so paging on a
/// non-unique sort key (a <c>Name</c>, a <c>ServiceDateUtc</c>) lets page 2 repeat a row from
/// page 1 or skip one, with a <c>Total</c> that says neither happened. Each read therefore ends
/// its ordering on a unique column: the primary key, or <c>ProgramId</c> for one traveler's
/// memberships, which <c>UNIQUE(TravelerId, ProgramId)</c> makes unique within the filter.
/// </para>
///
/// <para>
/// <b>Why these assert on the SQL.</b> On a small, freshly truncated table Postgres
/// seq-scans in insertion order whether or not a tiebreaker is there, so a "pages are disjoint"
/// test passes without the fix (the same finding as <c>BrowsableDataRepositoryTests</c>' I2).
/// The assertion is on the <c>ORDER BY</c> immediately before <c>LIMIT</c>: for a query with a
/// collection <c>Include</c>, EF pages in an inner subquery and appends keys only to the
/// <i>outer</i> <c>ORDER BY</c>, which is too late to make the page itself deterministic.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class PagedRepositoryDeterministicOrderTests : PostgresTestBase
{
    private readonly PostgresContainerFixture _fixture;

    public PagedRepositoryDeterministicOrderTests(PostgresContainerFixture fixture) : base(fixture)
    {
        _fixture = fixture;
    }

    private sealed class CommandCapturingInterceptor : DbCommandInterceptor
    {
        public List<string> CommandTexts { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandTexts.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private (FlightAssistantDbContext Db, CommandCapturingInterceptor Capture) CreateCapturingDbContext()
    {
        var capture = new CommandCapturingInterceptor();
        var db = new FlightAssistantDbContext(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(capture)
            .Options);
        return (db, capture);
    }

    private static readonly Regex OrderByBeforeLimit = new(
        @"ORDER BY (?<columns>[^()]+?)\s+LIMIT", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>The columns of the <c>ORDER BY</c> the page itself is taken from.</summary>
    private static string PagedOrderByColumns(CommandCapturingInterceptor capture)
    {
        var paged = Assert.Single(capture.CommandTexts, sql => sql.Contains("LIMIT", StringComparison.Ordinal));
        var match = OrderByBeforeLimit.Match(paged);
        Assert.True(match.Success, $"Expected an ORDER BY immediately before LIMIT, got:\n{paged}");
        return match.Groups["columns"].Value;
    }

    private static void AssertLastSortColumnIs(string column, CommandCapturingInterceptor capture)
    {
        var columns = PagedOrderByColumns(capture);
        Assert.True(
            columns.TrimEnd().EndsWith($".\"{column}\"", StringComparison.Ordinal),
            $"Expected the paged ORDER BY to end on the unique column \"{column}\", got: ORDER BY {columns}");
    }

    private static FlightRepository NewFlightRepository(FlightAssistantDbContext db) =>
        new(db, TimeProvider.System);

    // ── The paged ORDER BY ends on a unique column ──────────────────────────────

    [Fact]
    public async Task LoyaltyRepository_GetMembershipsForTravelerAsync_OrdersByProgramId()
    {
        var (db, capture) = CreateCapturingDbContext();
        await using (db)
            await new LoyaltyRepository(db).GetMembershipsForTravelerAsync(Guid.NewGuid(), page: 1, pageSize: 10);

        AssertLastSortColumnIs(nameof(TravelerLoyaltyMembership.ProgramId), capture);
    }

    [Fact]
    public async Task LoyaltyRepository_GetProgramsAsync_EndsOnIdTiebreaker()
    {
        var (db, capture) = CreateCapturingDbContext();
        await using (db)
            await new LoyaltyRepository(db).GetProgramsAsync(page: 1, pageSize: 10);

        AssertLastSortColumnIs("Id", capture);
    }

    [Fact]
    public async Task AirlineRepository_GetAllAsync_EndsOnIdTiebreaker()
    {
        var (db, capture) = CreateCapturingDbContext();
        await using (db)
            await new AirlineRepository(db).GetAllAsync(page: 1, pageSize: 10);

        AssertLastSortColumnIs("Id", capture);
    }

    [Fact]
    public async Task AirportRepository_GetAllAsync_EndsOnIdTiebreaker()
    {
        var (db, capture) = CreateCapturingDbContext();
        await using (db)
            await new AirportRepository(db).GetAllAsync(page: 1, pageSize: 10);

        AssertLastSortColumnIs("Id", capture);
    }

    [Fact]
    public async Task LoungeRepository_GetAllAsync_EndsOnIdTiebreaker()
    {
        var (db, capture) = CreateCapturingDbContext();
        await using (db)
            await new LoungeRepository(db).GetAllAsync(page: 1, pageSize: 10);

        AssertLastSortColumnIs("Id", capture);
    }

    [Fact]
    public async Task AdminFlightRepository_GetAllAsync_EndsOnIdTiebreaker()
    {
        var (db, capture) = CreateCapturingDbContext();
        await using (db)
            await new AdminFlightRepository(db).GetAllAsync(page: 1, pageSize: 10);

        AssertLastSortColumnIs("Id", capture);
    }

    [Fact]
    public async Task FlightRepository_GetTravelerFlightsAsync_EndsOnIdTiebreaker()
    {
        var (db, capture) = CreateCapturingDbContext();
        await using (db)
            await NewFlightRepository(db).GetTravelerFlightsAsync(Guid.NewGuid(), page: 1, pageSize: 10);

        AssertLastSortColumnIs("Id", capture);
    }

    // ── Behaviour: pages partition the set ──────────────────────────────────────

    /// <summary>
    /// The issue's own acceptance test: consecutive pages are disjoint and together cover every
    /// membership, in <c>ProgramId</c> order. Memberships are inserted in <i>descending</i>
    /// <c>ProgramId</c> order so that insertion order, which an unordered seq-scan returns, is
    /// distinguishable from the required order.
    /// </summary>
    [Fact]
    public async Task LoyaltyRepository_GetMembershipsForTravelerAsync_PagesAreDisjointAndCoverTheSetInProgramIdOrder()
    {
        await using var db = CreateDbContext();
        var traveler = Traveler.Register($"{Guid.NewGuid():N}@test.local");
        var kind = LoyaltyProgramKind.Create("AIRLINE", "Airline Program");
        db.Travelers.Add(traveler);
        db.LoyaltyProgramKinds.Add(kind);
        await db.SaveChangesAsync();

        var programs = Enumerable.Range(1, 5)
            .Select(i => LoyaltyProgram.Create($"PROG-{i}", $"Program {i}", kind.Id))
            .ToList();
        db.LoyaltyPrograms.AddRange(programs);
        await db.SaveChangesAsync();

        foreach (var program in programs.OrderByDescending(p => p.Id))
        {
            db.TravelerLoyaltyMemberships.Add(TravelerLoyaltyMembership.Create(traveler.Id, program.Id, $"ACC-{program.Id}"));
            await db.SaveChangesAsync();
        }

        var repo = new LoyaltyRepository(db);
        var pages = new List<IReadOnlyList<TravelerLoyaltyMembership>>();
        for (var page = 1; page <= 3; page++)
        {
            var (items, total) = await repo.GetMembershipsForTravelerAsync(traveler.Id, page, pageSize: 2);
            Assert.Equal(5, total);
            pages.Add(items);
        }

        var returned = pages.SelectMany(p => p).Select(m => m.ProgramId).ToList();
        Assert.Equal(programs.Select(p => p.Id).Order(), returned);
    }
}
