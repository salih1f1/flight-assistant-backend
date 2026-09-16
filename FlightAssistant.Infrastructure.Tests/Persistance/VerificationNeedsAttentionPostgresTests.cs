using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// <see cref="VerificationNeedsAttentionRepository"/> against real PostgreSQL (#385, Task 2): the
/// counts behind the admin panel's "Needs attention" card. What is under test is real SQL translation
/// of the needs-attention predicate (spec §2.1: <c>IsVerified</c> false <b>and</b>
/// <c>VerificationIssue</c> non-null and non-blank) -- <c>string.IsNullOrWhiteSpace</c> does not
/// translate in EF Core, and InMemory would happily accept it (or a plain <c>IsVerified == false</c>,
/// or a query that simply omits an empty table) without ever showing the difference.
/// </summary>
[Collection(PostgresCollection.Name)]
public class VerificationNeedsAttentionPostgresTests : PostgresTestBase
{
    public VerificationNeedsAttentionPostgresTests(PostgresContainerFixture fixture) : base(fixture) { }

    private static VerificationNeedsAttentionRepository Repository(FlightAssistantDbContext db) => new(db);

    /// <summary>
    /// Break-proof 3: an implementation that omits a type with a zero count (e.g. builds its answer
    /// only from rows it actually finds) returns fewer than three entries here, where every table is
    /// empty and every count must be zero.
    /// </summary>
    [Fact]
    public async Task GetCountsAsync_NoRowsAnywhere_ReturnsAllThreeTypesAtZero_InEnumOrder()
    {
        await using var db = CreateDbContext();

        var counts = await Repository(db).GetCountsAsync();

        Assert.Equal(
            new[]
            {
                new VerificationNeedsAttentionCount(VerificationEntityType.Airport, 0),
                new VerificationNeedsAttentionCount(VerificationEntityType.Airline, 0),
                new VerificationNeedsAttentionCount(VerificationEntityType.Country, 0),
            },
            counts);
    }

    /// <summary>
    /// Break-proof 1: an implementation that counts <c>IsVerified == false</c> alone, ignoring
    /// <c>VerificationIssue</c>, also counts each type's plainly-unverified row (and Country's plainly
    /// unverified "US", which doubles as the FK anchor Airport/Airline need) -- doubling every count
    /// here from 1 to 2. Seeding all three states for all three types in one test, and asserting each
    /// type's count individually, also proves a type's rows never leak into another type's count: a
    /// query that accidentally scanned another table would push one of these counts above 1 too.
    /// </summary>
    [Fact]
    public async Task GetCountsAsync_CountsOnlyTheUnverifiedWithAnIssueState_AcrossAllThreeTypes_WithNoCrossTypeLeakage()
    {
        await using var db = CreateDbContext();

        // Country: "US" is both the FK anchor Airport/Airline need below and this type's "plainly
        // unverified" row (freshly created, never verified, no issue).
        var plainCountry = Country.Create("US", "United States");
        var verifiedCountry = Country.Create("GB", "United Kingdom");
        verifiedCountry.Verify("admin@example.com", "manual");
        var issueCountry = Country.Create("FR", "France");
        issueCountry.MarkVerificationFailed("Name mismatch");
        db.Countries.AddRange(plainCountry, verifiedCountry, issueCountry);

        var verifiedAirport = Airport.Create("AAA", null, "Verified Airport", "Testville", "US");
        verifiedAirport.Verify("admin@example.com", "manual");
        var plainAirport = Airport.Create("BBB", null, "Plain Airport", "Testville", "US");
        var issueAirport = Airport.Create("CCC", null, "Issue Airport", "Testville", "US");
        issueAirport.MarkVerificationFailed("Name mismatch");
        db.Airports.AddRange(verifiedAirport, plainAirport, issueAirport);

        var verifiedAirline = Airline.Create("AA", "AAL", "Verified Airline", "US");
        verifiedAirline.Verify("admin@example.com", "manual");
        var plainAirline = Airline.Create("BB", "BBL", "Plain Airline", "US");
        var issueAirline = Airline.Create("CC", "CCL", "Issue Airline", "US");
        issueAirline.MarkVerificationFailed("Name mismatch");
        db.Airlines.AddRange(verifiedAirline, plainAirline, issueAirline);

        await db.SaveChangesAsync();

        var counts = (await Repository(db).GetCountsAsync()).ToDictionary(c => c.EntityType, c => c.Count);

        Assert.Equal(1, counts[VerificationEntityType.Airport]); // issueAirport only
        Assert.Equal(1, counts[VerificationEntityType.Airline]); // issueAirline only
        Assert.Equal(1, counts[VerificationEntityType.Country]); // issueCountry ("FR") only
    }

    /// <summary>
    /// Break-proof 2: an implementation that counts any non-null <c>VerificationIssue</c>, ignoring
    /// whitespace, counts this row too. <see cref="VerificationGuard.EnsureValidIssue"/> refuses a
    /// blank issue through <c>MarkVerificationFailed</c>, so a whitespace-only value is written
    /// directly onto the tracked property (as <see cref="SeedableEntity.FirstSeenAt"/> is overridden
    /// the same way in <c>AdminStatsRepositoryTests</c>) to prove the predicate itself, not just the
    /// code path that normally produces it.
    /// </summary>
    [Fact]
    public async Task GetCountsAsync_AWhitespaceOnlyIssue_DoesNotCount()
    {
        await using var db = CreateDbContext();
        db.Countries.Add(Country.Create("US", "United States"));
        var whitespaceAirport = Airport.Create("DDD", null, "Whitespace Issue Airport", "Testville", "US");
        db.Airports.Add(whitespaceAirport);
        db.Entry(whitespaceAirport).Property(a => a.VerificationIssue).CurrentValue = "   ";
        await db.SaveChangesAsync();

        var counts = (await Repository(db).GetCountsAsync()).ToDictionary(c => c.EntityType, c => c.Count);

        Assert.Equal(0, counts[VerificationEntityType.Airport]);
    }
}
