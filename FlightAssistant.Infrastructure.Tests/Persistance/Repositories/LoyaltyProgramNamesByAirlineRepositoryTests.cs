using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// <c>GetProgramNamesByAirlineIdsAsync</c> (#495) against real PostgreSQL. What is worth a
/// container rather than a substitute is the SQL: that a NULL <c>AirlineId</c> never falls into
/// the result — comparing NULL to a value is unknown, not true — and that an airline running more
/// than one program names the same one twice running, which PostgreSQL guarantees only under an
/// ORDER BY.
/// </summary>
[Collection(PostgresCollection.Name)]
public class LoyaltyProgramNamesByAirlineRepositoryTests : PostgresTestBase
{
    public LoyaltyProgramNamesByAirlineRepositoryTests(PostgresContainerFixture fixture) : base(fixture)
    {
    }

    /// <summary>
    /// Seeds three airlines and returns their ids: one with a single program, one with two, and one
    /// with none. A fourth program belongs to no airline at all.
    /// </summary>
    private async Task<(int WithOne, int WithTwo, int WithNone)> SeedAsync()
    {
        await using var db = CreateDbContext();
        db.Countries.Add(Country.Create("US", "United States", "USA", 840));
        var withOne = Airline.Create("AA", "AAL", "American Airlines", "US", null);
        var withTwo = Airline.Create("BA", "BAW", "British Airways", "US", null);
        var withNone = Airline.Create("U2", "EZY", "easyJet", "US", null);
        db.Airlines.AddRange(withOne, withTwo, withNone);
        var kind = LoyaltyProgramKind.Create("AIRLINE", "Airline");
        db.LoyaltyProgramKinds.Add(kind);
        await db.SaveChangesAsync();

        db.LoyaltyPrograms.AddRange(
            LoyaltyProgram.Create("AADV", "AAdvantage", kind.Id, withOne.Id),
            // Inserted in the opposite order to the one they come back in, so "first by name"
            // cannot pass by accident on insertion order.
            LoyaltyProgram.Create("BAEC", "Executive Club", kind.Id, withTwo.Id),
            LoyaltyProgram.Create("BAAV", "Avios", kind.Id, withTwo.Id),
            // No airline: an alliance-wide program, and never anyone's answer.
            LoyaltyProgram.Create("OWRP", "oneworld Rewards", kind.Id, null));
        await db.SaveChangesAsync();

        return (withOne.Id, withTwo.Id, withNone.Id);
    }

    [Fact]
    public async Task GetProgramNamesByAirlineIdsAsync_NamesEachAirlinesProgram()
    {
        var (withOne, _, _) = await SeedAsync();
        await using var db = CreateDbContext();
        var repo = new LoyaltyRepository(db);

        var names = await repo.GetProgramNamesByAirlineIdsAsync([withOne]);

        Assert.Equal("AAdvantage", names[withOne]);
    }

    /// <summary>
    /// An airline with no program of its own is absent from the dictionary, not present with a
    /// null — the caller's <c>TryGetValue</c> is what turns that into the row's empty cell.
    /// </summary>
    [Fact]
    public async Task GetProgramNamesByAirlineIdsAsync_AirlineWithNoProgram_IsAbsent()
    {
        var (withOne, _, withNone) = await SeedAsync();
        await using var db = CreateDbContext();
        var repo = new LoyaltyRepository(db);

        var names = await repo.GetProgramNamesByAirlineIdsAsync([withOne, withNone]);

        Assert.False(names.ContainsKey(withNone));
    }

    /// <summary>
    /// A program with a NULL AirlineId belongs to no airline. Getting this wrong would put an
    /// alliance-wide program's name on whichever airline row happened to be rendered.
    /// </summary>
    [Fact]
    public async Task GetProgramNamesByAirlineIdsAsync_ProgramWithNoAirline_IsNeverReturned()
    {
        var (withOne, withTwo, withNone) = await SeedAsync();
        await using var db = CreateDbContext();
        var repo = new LoyaltyRepository(db);

        var names = await repo.GetProgramNamesByAirlineIdsAsync([withOne, withTwo, withNone]);

        Assert.DoesNotContain("oneworld Rewards", names.Values);
    }

    [Fact]
    public async Task GetProgramNamesByAirlineIdsAsync_AirlineWithSeveralPrograms_NamesTheFirstByName()
    {
        var (_, withTwo, _) = await SeedAsync();
        await using var db = CreateDbContext();
        var repo = new LoyaltyRepository(db);

        var names = await repo.GetProgramNamesByAirlineIdsAsync([withTwo]);

        Assert.Equal("Avios", names[withTwo]);
    }

    /// <summary>An empty ask is an empty answer, without a round trip for an <c>IN ()</c>.</summary>
    [Fact]
    public async Task GetProgramNamesByAirlineIdsAsync_NoAirlineIds_IsEmpty()
    {
        await SeedAsync();
        await using var db = CreateDbContext();
        var repo = new LoyaltyRepository(db);

        Assert.Empty(await repo.GetProgramNamesByAirlineIdsAsync([]));
    }
}
