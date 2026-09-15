using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// <see cref="CountryRepository"/>'s unique-column lookups against real PostgreSQL (#378). Accept-source
/// uses them to refuse an Alpha3 or NumericCode another country already has, before the filtered unique
/// indexes on <c>countries</c> would fail the save. PostgreSQL rather than InMemory because
/// <c>Alpha3</c> is a fixed-length <c>char(3)</c> column, and the comparison against it is the database's.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CountryRepositoryTests : PostgresTestBase
{
    public CountryRepositoryTests(PostgresContainerFixture fixture) : base(fixture) { }

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();
        db.Countries.AddRange(
            Country.Create("GB", "United Kingdom", "GBR", 826),
            Country.Create("FR", "France", "FRA", 250),
            Country.Create("XK", "Kosovo")); // neither code: null is never a match
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task FindByAlpha3Async_FindsTheCountryOwningTheCode_InAnyCase_AndNullForAFreeCode()
    {
        await SeedAsync();
        await using var db = CreateDbContext();
        var repository = new CountryRepository(db);

        Assert.Equal("FR", (await repository.FindByAlpha3Async("fra"))?.Code);
        Assert.Equal("GB", (await repository.FindByAlpha3Async("GBR"))?.Code);
        Assert.Null(await repository.FindByAlpha3Async("DEU"));
    }

    [Fact]
    public async Task FindByNumericCodeAsync_FindsTheCountryOwningTheCode_AndNullForAFreeCode()
    {
        await SeedAsync();
        await using var db = CreateDbContext();
        var repository = new CountryRepository(db);

        Assert.Equal("GB", (await repository.FindByNumericCodeAsync(826))?.Code);
        Assert.Equal("FR", (await repository.FindByNumericCodeAsync(250))?.Code);
        Assert.Null(await repository.FindByNumericCodeAsync(276));
    }
}
