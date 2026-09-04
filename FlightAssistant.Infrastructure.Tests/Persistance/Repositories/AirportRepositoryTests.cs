using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// <see cref="AirportRepository.GetAllAsync"/> against real PostgreSQL — the backstop half of #316
/// (<see cref="FlightAssistant.Application.Validators.PagedRequestValidator"/>'s overflow bound, covered by
/// <c>ValidatorEdgeCaseTests</c>, is the user-facing 400 half). A Page large enough that
/// <c>(page - 1) * pageSize</c> overflows a 32-bit int used to reach Npgsql as a negative
/// <c>OFFSET</c> and throw; every paged repository now routes through
/// <see cref="PagedQueryableExtensions.Page{T}"/> instead of computing that arithmetic itself.
///
/// <para>
/// <b>Why one repository, not all ten.</b> The offset arithmetic used to be duplicated across all
/// ten paged repositories; it now lives in exactly one place
/// (<see cref="PagedQueryableExtensions.Page{T}"/>), and every one of the ten call sites is a
/// one-line, mechanical call into it. The thing actually under test here is that shared helper, not
/// any particular repository's query shape — ten near-identical Postgres tests, one per repository,
/// would prove nothing this one doesn't already prove. <see cref="AirportRepository"/> is the
/// vehicle because its fixture is the cheapest of the ten: one FK-referenced <see cref="Country"/>
/// row and nothing else to set up.
/// </para>
///
/// <para>
/// <b>Why this needs real PostgreSQL and cannot run on InMemory.</b> InMemory's LINQ provider
/// accepts a negative <c>Skip</c> without complaint — it is evaluated client-side, so there is no
/// SQL <c>OFFSET</c> for it to reject. Only a real Npgsql translation throws
/// <c>OFFSET must not be negative</c>. A test against InMemory would pass whether or not the clamp
/// in <see cref="PagedQueryableExtensions.Page{T}"/> existed at all, which is exactly the kind of
/// test this repo's mutation-proof standard treats as worthless.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class AirportRepositoryTests : PostgresTestBase
{
    public AirportRepositoryTests(PostgresContainerFixture fixture) : base(fixture) { }

    private static AirportRepository Repository(FlightAssistantDbContext db) => new(db);

    /// <summary>
    /// The #316 regression itself: a Page large enough that <c>(page - 1) * pageSize</c> overflows
    /// a 32-bit int — the classic "wraps around to a negative OFFSET" shape — must not throw against
    /// real Postgres, and must come back as an honest empty page rather than any row from the table.
    /// PageSize = 200 (the maximum <c>PagedRequestValidator</c> allows) is what makes
    /// <c>int.MaxValue</c> actually overflow the multiplication; PageSize = 1 would not reach the
    /// bug at this Page value (see <c>ValidatorEdgeCaseTests</c>' own PageSize-dependent case for the
    /// same point made against the validator).
    /// </summary>
    [Fact]
    public async Task GetAllAsync_PageAndPageSizeOverflowTheOffset_ReturnsEmptyPage_NotAnException()
    {
        await using var db = CreateDbContext();
        db.Countries.Add(Country.Create("US", "United States"));
        db.Airports.AddRange(
            Airport.Create("AAA", null, "Airport A", "City A", "US"),
            Airport.Create("BBB", null, "Airport B", "City B", "US"),
            Airport.Create("CCC", null, "Airport C", "City C", "US"));
        await db.SaveChangesAsync();

        (IReadOnlyList<Airport> Items, int Total) result = (Array.Empty<Airport>(), -1);
        var exception = await Record.ExceptionAsync(async () =>
            result = await Repository(db).GetAllAsync(page: int.MaxValue, pageSize: 200));

        Assert.Null(exception);
        Assert.Empty(result.Items);
        // Total is still the real row count — a clamped offset changes which page comes back, not
        // what the table actually contains.
        Assert.Equal(3, result.Total);
    }

    /// <summary>
    /// The other half of the refactor's safety net: converting all ten call sites to the shared
    /// helper must not silently change what an ordinary, in-range page returns. Three airports,
    /// pageSize = 2: page 1 returns the first two (alphabetically, per <c>GetAllAsync</c>'s own
    /// <c>ORDER BY Name</c>), page 2 returns exactly the one remaining row — proving both the offset
    /// and the limit side of <see cref="PagedQueryableExtensions.Page{T}"/> still compose correctly.
    /// </summary>
    [Fact]
    public async Task GetAllAsync_OrdinaryInRangePage_StillReturnsTheCorrectRows()
    {
        await using var db = CreateDbContext();
        db.Countries.Add(Country.Create("US", "United States"));
        db.Airports.AddRange(
            Airport.Create("AAA", null, "Airport A", "City A", "US"),
            Airport.Create("BBB", null, "Airport B", "City B", "US"),
            Airport.Create("CCC", null, "Airport C", "City C", "US"));
        await db.SaveChangesAsync();

        var (firstPageItems, firstPageTotal) = await Repository(db).GetAllAsync(page: 1, pageSize: 2);
        Assert.Equal(3, firstPageTotal);
        Assert.Equal(["Airport A", "Airport B"], firstPageItems.Select(a => a.Name));

        var (secondPageItems, secondPageTotal) = await Repository(db).GetAllAsync(page: 2, pageSize: 2);
        Assert.Equal(3, secondPageTotal);
        Assert.Equal(["Airport C"], secondPageItems.Select(a => a.Name));
    }
}
