using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// <see cref="AdminStatsRepository"/> against real PostgreSQL (#336): the counts behind the
/// dashboard cards. What each test pins is which rows count as "added" in a window. The window is
/// start-inclusive and end-exclusive, a seeded airline or airport never counts, and neither does a
/// lounge or program with no creation date. All of these are SQL comparisons on <c>timestamptz</c>
/// columns, which InMemory would not translate.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AdminStatsRepositoryTests : PostgresTestBase
{
    public AdminStatsRepositoryTests(PostgresContainerFixture fixture) : base(fixture) { }

    private static readonly DateTime From = new(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);

    private static AdminStatsRepository Repository(FlightAssistantDbContext db) => new(db);

    private static async Task<IReadOnlyDictionary<string, AdminEntityCounts>> CountsAsync(
        FlightAssistantDbContext db, DateTime from, DateTime to) =>
        (await Repository(db).GetCountsAsync(from, to)).ToDictionary(c => c.Key);

    [Fact]
    public async Task GetCountsAsync_ReturnsTheSixCards_InOrder_WithTheirDateColumns()
    {
        await using var db = CreateDbContext();

        var counts = await Repository(db).GetCountsAsync(From, To);

        Assert.Equal(
            ["travelers", "flights", "airlines", "airports", "lounges", "loyalty-programs"],
            counts.Select(c => c.Key));
        Assert.Equal(
            ["CreatedUtc", "CreatedUtc", "FirstSeenAt", "FirstSeenAt", "CreatedUtc", "CreatedUtc"],
            counts.Select(c => c.DateColumn));
        Assert.All(counts, c => Assert.Equal(0, c.Total));
        Assert.All(counts, c => Assert.Empty(c.AddedUtc));
        // Travelers and flights have no verified state; every other card counts it, even at zero.
        Assert.Equal([null, null, 0, 0, 0, 0], counts.Select(c => c.Verified));
    }

    [Fact]
    public async Task GetCountsAsync_Travelers_AddedIsTheWindowOnly_StartInclusive_EndExclusive()
    {
        await using var db = CreateDbContext();
        void AddTraveler(DateTime createdUtc)
        {
            var traveler = Traveler.Register($"{Guid.NewGuid():N}@example.com", null);
            db.Travelers.Add(traveler);
            db.Entry(traveler).Property(t => t.CreatedUtc).CurrentValue = createdUtc;
        }
        AddTraveler(From.AddMilliseconds(-1));
        AddTraveler(From);
        AddTraveler(To.AddMilliseconds(-1));
        AddTraveler(To);
        await db.SaveChangesAsync();

        var travelers = (await CountsAsync(db, From, To))["travelers"];

        Assert.Equal(4, travelers.Total);
        Assert.Equal([From, To.AddMilliseconds(-1)], travelers.AddedUtc.Order());
    }

    [Fact]
    public async Task GetCountsAsync_AirlinesAndAirports_SeededRowsAreNeverNew_AndVerifiedIsCounted()
    {
        await using var db = CreateDbContext();
        db.Countries.Add(Country.Create("US", "United States"));
        var inWindow = From.AddDays(2);
        T Add<T>(T entity, string sourceType, DateTime firstSeenAt, bool verified = false) where T : SeedableEntity
        {
            entity.SourceType = sourceType;
            if (verified) entity.Verify("admin@example.com");
            db.Add(entity);
            db.Entry(entity).Property(e => e.FirstSeenAt).CurrentValue = firstSeenAt;
            return entity;
        }
        Add(Airline.Create("AA", "AAL", "Seeded Air", "US"), "seed", inWindow, verified: true);
        Add(Airline.Create("BB", "BBL", "Imported Air", "US"), "import", inWindow);
        Add(Airline.Create("CC", "CCL", "Old Import Air", "US"), "import", From.AddDays(-30));
        Add(Airport.Create("AAA", null, "Seeded Airport", "City", "US"), "seed", inWindow);
        Add(Airport.Create("BBB", null, "Admin Airport", "City", "US"), "admin", inWindow, verified: true);
        await db.SaveChangesAsync();

        var counts = await CountsAsync(db, From, To);

        var airlines = counts["airlines"];
        Assert.Equal((3, (int?)1), (airlines.Total, airlines.Verified));
        Assert.Equal([inWindow], airlines.AddedUtc);
        var airports = counts["airports"];
        Assert.Equal((2, (int?)1), (airports.Total, airports.Verified));
        Assert.Equal([inWindow], airports.AddedUtc);
    }

    [Fact]
    public async Task GetCountsAsync_LoungesAndPrograms_RowsWithNoCreationDateAreNeverNew()
    {
        await using var db = CreateDbContext();
        db.Countries.Add(Country.Create("US", "United States"));
        var airport = Airport.Create("AAA", null, "Airport", "City", "US");
        db.Airports.Add(airport);
        var kind = LoyaltyProgramKind.Create("AIRLINE", "Airline");
        db.LoyaltyProgramKinds.Add(kind);
        await db.SaveChangesAsync();

        var added = Lounge.Create(airport.Id, "Added Lounge");
        added.Verify("admin@example.com");
        db.Lounges.AddRange(added, Lounge.CreateSeeded(airport.Id, "Seeded Lounge"));
        db.LoyaltyPrograms.AddRange(
            LoyaltyProgram.Create("NEW", "Added Program", kind.Id),
            LoyaltyProgram.CreateSeeded("SEED", "Seeded Program", kind.Id));
        await db.SaveChangesAsync();

        // Create stamps "now", so the window is around now.
        var now = DateTime.UtcNow;
        var counts = await CountsAsync(db, now.AddHours(-1), now.AddHours(1));

        var lounges = counts["lounges"];
        Assert.Equal((2, (int?)1), (lounges.Total, lounges.Verified));
        Assert.Single(lounges.AddedUtc);
        var programs = counts["loyalty-programs"];
        Assert.Equal((2, (int?)0), (programs.Total, programs.Verified));
        Assert.Single(programs.AddedUtc);
    }
}
