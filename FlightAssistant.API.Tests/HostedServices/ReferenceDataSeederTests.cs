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
    }
}
