using FlightAssistant.API.HostedServices;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightAssistant.API.Tests.HostedServices;

/// <summary>
/// Exercises <see cref="ReferenceDataSeeder"/> end-to-end against the real
/// <c>SeedData/{Alliance,Country,Airport,Airline}/*.json</c> files (copied to the test
/// output directory by the API project's content-copy mechanism), using a real
/// <see cref="FlightAssistantDbContext"/> backed by the EF Core in-memory provider.
///
/// Regression test for #209: the seeder used to deserialize JSON directly into the
/// domain entities (which have private setters and private constructors), causing
/// deserialization to throw and the seed files to be silently skipped. This test
/// asserts the reference-data tables are actually populated with real values from
/// the seed files.
/// </summary>
public class ReferenceDataSeederTests
{
    [Fact]
    public async Task StartAsync_PopulatesReferenceDataFromRealSeedFiles()
    {
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddDbContext<FlightAssistantDbContext>(o =>
            o.UseInMemoryDatabase(dbName));

        await using var provider = services.BuildServiceProvider();

        var seeder = new ReferenceDataSeeder(provider, NullLogger<ReferenceDataSeeder>.Instance);

        await seeder.StartAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();

        // Alliances (alliances.v1.json: SA/Star Alliance, OW/oneworld, ST/SkyTeam)
        var starAlliance = await db.Alliances.SingleOrDefaultAsync(a => a.Code == "SA");
        Assert.NotNull(starAlliance);
        Assert.Equal("Star Alliance", starAlliance!.Name);
        Assert.Equal(3, await db.Alliances.CountAsync());

        // Countries (countries.v1.json includes Afghanistan / AF / AFG)
        var afghanistan = await db.Countries.SingleOrDefaultAsync(c => c.Code == "AF");
        Assert.NotNull(afghanistan);
        Assert.Equal("Afghanistan", afghanistan!.Name);
        Assert.Equal("AFG", afghanistan.Alpha3);
        Assert.Equal(4, afghanistan.NumericCode);
        Assert.True(await db.Countries.CountAsync() > 1);

        // Airports (airports.v1.json includes AAA / Anaa Airport)
        var anaa = await db.Airports.SingleOrDefaultAsync(a => a.Iata == "AAA");
        Assert.NotNull(anaa);
        Assert.Equal("Anaa Airport", anaa!.Name);
        Assert.Equal("Anaa", anaa.City);
        Assert.Equal("PF", anaa.CountryCode);
        Assert.Equal("NTGA", anaa.Icao);
        Assert.True(await db.Airports.CountAsync() > 1);

        // Airlines (airlines.v1.json includes EK / Emirates)
        var emirates = await db.Airlines.SingleOrDefaultAsync(a => a.Iata == "EK");
        Assert.NotNull(emirates);
        Assert.Equal("Emirates", emirates!.Name);
        Assert.Equal("AE", emirates.CountryCode);
        Assert.Equal("UAE", emirates.Icao);
        Assert.True(await db.Airlines.CountAsync() > 1);

        // Lounges and loyalty programs from the seed files have no creation date (#334): they
        // weren't added by anyone, and a fresh environment must not report its whole seed as new.
        var lounges = await db.Lounges.ToListAsync();
        Assert.NotEmpty(lounges);
        Assert.All(lounges, l => Assert.Null(l.CreatedUtc));
        var programs = await db.LoyaltyPrograms.ToListAsync();
        Assert.NotEmpty(programs);
        Assert.All(programs, p => Assert.Null(p.CreatedUtc));
    }

    /// <summary>
    /// #504: an alliance is an airline alliance, so the seed asserts it on the airline. The 21
    /// airlines the loyalty seed names carry their alliance (British Airways stands in for the
    /// list), and the two airlines that seed referenced without containing — Cathay Pacific and
    /// Korean Air — now exist, so their programs find an airline to link to.
    /// </summary>
    [Fact]
    public async Task StartAsync_GivesAirlinesTheirAllianceAndSeedsTheAirlinesProgramsReference()
    {
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddDbContext<FlightAssistantDbContext>(o => o.UseInMemoryDatabase(dbName));

        await using var provider = services.BuildServiceProvider();
        var seeder = new ReferenceDataSeeder(provider, NullLogger<ReferenceDataSeeder>.Instance);

        await seeder.StartAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();

        // airlines.v1.json — the alliance now lives on the airline.
        var britishAirways = await db.Airlines.SingleOrDefaultAsync(a => a.Iata == "BA");
        Assert.NotNull(britishAirways);
        Assert.Equal("OW", britishAirways!.AllianceCode);

        var lufthansa = await db.Airlines.SingleOrDefaultAsync(a => a.Iata == "LH");
        Assert.Equal("SA", lufthansa!.AllianceCode);

        // An airline with no alliance stays without one.
        var emirates = await db.Airlines.SingleOrDefaultAsync(a => a.Iata == "EK");
        Assert.Null(emirates!.AllianceCode);

        // airlines.v2.json — the two airlines the loyalty seed referenced but the airline seed lacked.
        var cathay = await db.Airlines.SingleOrDefaultAsync(a => a.Iata == "CX");
        Assert.NotNull(cathay);
        Assert.Equal("Cathay Pacific", cathay!.Name);
        Assert.Equal("OW", cathay.AllianceCode);

        var korean = await db.Airlines.SingleOrDefaultAsync(a => a.Iata == "KE");
        Assert.NotNull(korean);
        Assert.Equal("ST", korean!.AllianceCode);

        // Airlines seed before loyalty programs, so these two now link instead of logging a warning.
        var cathayProgram = await db.LoyaltyPrograms.SingleAsync(p => p.Code == "CX-MPC");
        Assert.Equal(cathay.Id, cathayProgram.AirlineId);

        var koreanProgram = await db.LoyaltyPrograms.SingleAsync(p => p.Code == "KE-SKY");
        Assert.Equal(korean.Id, koreanProgram.AirlineId);
    }
}
