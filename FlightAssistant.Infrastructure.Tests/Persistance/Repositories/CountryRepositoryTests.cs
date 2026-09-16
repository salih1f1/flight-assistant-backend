using System.Data.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
    private readonly PostgresContainerFixture _fixture;

    public CountryRepositoryTests(PostgresContainerFixture fixture) : base(fixture)
    {
        _fixture = fixture;
    }

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

    /// <summary>
    /// #410 fix round, I3/Fix 2: <see cref="FlightAssistant.Application.EventHandlers.FlightTrackedVerificationEventHandler"/>
    /// used to call <see cref="CountryRepository.FindByCodeAsync"/> once per distinct country code,
    /// inside a loop -- up to three sequential round trips on every track request. This proves the
    /// batched replacement returns exactly the matching rows in one query, de-duplicates repeated
    /// input codes down to one row per code (the table's own primary key on <c>Code</c> makes that
    /// automatic once the query runs), is case-insensitive the same way <see cref="CountryRepository.FindByCodeAsync"/>
    /// is, and simply omits a code with no matching row rather than erroring.
    /// </summary>
    [Fact]
    public async Task FindByCodesAsync_ReturnsOnlyMatchingRows_DeDuplicated_CaseInsensitive()
    {
        await SeedAsync();
        await using var db = CreateDbContext();
        var repository = new CountryRepository(db);

        // "fr" lower-case and "GB" repeated twice -- both the case-insensitivity and the
        // de-duplication get exercised in the same call. "US" matches nothing.
        var result = await repository.FindByCodesAsync(new[] { "fr", "GB", "GB", "US" });

        Assert.Equal(2, result.Count);
        Assert.Contains(result, c => c.Code == "FR");
        Assert.Contains(result, c => c.Code == "GB");
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>An empty input must not hit the database at all -- the handler calls this
    /// unconditionally on every track request, so the common case (no reference row has gone
    /// stale, but the handler still resolves country codes -- see #410's I3) must not cost a round
    /// trip when there happen to be no codes to resolve. Proved the same way
    /// <c>AdminDrilldownRepositoryTests</c> proves "one query, not N+1": a real
    /// <see cref="DbCommandInterceptor"/> against the live container, since the InMemory provider
    /// never issues a <see cref="DbCommand"/> at all and so could not distinguish "queried and got
    /// zero rows back" from "never queried".</summary>
    [Fact]
    public async Task FindByCodesAsync_EmptyInput_ReturnsEmpty_WithoutQueryingTheDatabase()
    {
        var counter = new CommandCountingInterceptor();
        await using var db = new FlightAssistantDbContext(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(counter)
            .Options);
        var repository = new CountryRepository(db);

        var result = await repository.FindByCodesAsync(Array.Empty<string>());

        Assert.Empty(result);
        Assert.Equal(0, counter.Count);
    }
}
