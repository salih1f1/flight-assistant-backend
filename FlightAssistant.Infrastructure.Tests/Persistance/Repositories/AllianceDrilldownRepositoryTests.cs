using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// The alliance drill-down's two reads (#457) against real PostgreSQL. The filter and the ordering
/// are the database's, not ours: <c>Airline.AllianceCode</c> carries a value converter that
/// uppercases on write, so whether a lower-case argument matches is decided by how EF translates
/// the comparison — something the InMemory provider the API suite runs on cannot answer. The
/// ordering matters for the same reason it does on the other drill-downs: PostgreSQL guarantees
/// none of its own, so an unordered query can re-render the same alliance differently between two
/// identical calls.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AllianceDrilldownRepositoryTests : PostgresTestBase
{
    public AllianceDrilldownRepositoryTests(PostgresContainerFixture fixture) : base(fixture)
    {
    }

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();
        db.Countries.AddRange(
            Country.Create("GB", "United Kingdom", "GBR", 826),
            Country.Create("US", "United States", "USA", 840),
            Country.Create("DE", "Germany", "DEU", 276));
        db.Alliances.AddRange(
            Alliance.Create("OW", "oneworld"),
            Alliance.Create("SA", "Star Alliance"),
            Alliance.Create("ST", "SkyTeam"));
        // Insertion order is deliberately neither the name order nor its reverse: with only two
        // rows inserted back-to-front, "by name ascending" and "by id descending" are the same
        // sequence, and an ordering test cannot tell a correct query from a wrong one.
        db.Airlines.AddRange(
            Airline.Create("QF", "QFA", "Qantas", "GB", "OW"),
            Airline.Create("AA", "AAL", "American Airlines", "US", "OW"),
            Airline.Create("BA", "BAW", "British Airways", "GB", "OW"),
            Airline.Create("LH", "DLH", "Lufthansa", "DE", "SA"),
            Airline.Create("U2", "EZY", "easyJet", "GB", null)); // no alliance: never a match
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetByAllianceCodeAsync_ReturnsOnlyThatAlliancesAirlines_OrderedByName()
    {
        await SeedAsync();
        await using var db = CreateDbContext();
        var repo = new AirlineRepository(db);

        var airlines = await repo.GetByAllianceCodeAsync("OW");

        Assert.Equal(
            ["American Airlines", "British Airways", "Qantas"],
            airlines.Select(a => a.Name));
    }

    /// <summary>
    /// Case-insensitivity here is the <c>AllianceCode</c> value converter's doing, not the
    /// repository's <c>ToUpperInvariant</c>: EF applies the converter to the parameter as well as
    /// the column, so the match holds with or without it. The test pins the behaviour callers rely
    /// on rather than whichever of the two mechanisms currently delivers it.
    /// </summary>
    [Fact]
    public async Task GetByAllianceCodeAsync_MatchesInAnyCase()
    {
        await SeedAsync();
        await using var db = CreateDbContext();
        var repo = new AirlineRepository(db);

        var airlines = await repo.GetByAllianceCodeAsync("ow");

        Assert.Equal(3, airlines.Count);
    }

    /// <summary>
    /// An airline with no alliance must never fall into one. A <c>NULL</c> column compared to a
    /// value is unknown, not true, in SQL — worth pinning, because getting it wrong would put every
    /// unaffiliated airline into whichever alliance an admin happened to open.
    /// </summary>
    [Fact]
    public async Task GetByAllianceCodeAsync_AllianceWithNoAirlines_IsEmptyAndExcludesUnaffiliatedOnes()
    {
        await SeedAsync();
        await using var db = CreateDbContext();
        var repo = new AirlineRepository(db);

        var airlines = await repo.GetByAllianceCodeAsync("ST");

        Assert.Empty(airlines);
    }

    [Fact]
    public async Task FindByIdAsync_FindsTheAllianceByItsSurrogateId_AndNullForAnUnknownOne()
    {
        await SeedAsync();
        await using var db = CreateDbContext();
        var seeded = await db.Alliances.AsNoTracking().SingleAsync(a => a.Code == "SA");
        var repo = new AllianceRepository(db);

        Assert.Equal("Star Alliance", (await repo.FindByIdAsync(seeded.Id))?.Name);
        Assert.Null(await repo.FindByIdAsync(987654));
    }
}
