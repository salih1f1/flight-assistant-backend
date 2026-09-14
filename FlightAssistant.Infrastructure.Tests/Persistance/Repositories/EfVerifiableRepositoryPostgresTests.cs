using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// <c>Country.Code</c> is <c>char(2)</c> (<c>CountryConfiguration.HasMaxLength(2).IsFixedLength()</c>).
/// <c>AdminVerifyEndpoints</c>' country route (#331) canonicalises the route's <c>{code}</c>
/// segment and passes it straight through as <c>EfVerifiableRepository&lt;Country&gt;</c>'s lookup
/// key with no length check of its own — so a 3-char code (e.g. the route
/// <c>PATCH /admin/countries/NAN/verify</c>) only 404s if PostgreSQL genuinely refuses to match a
/// <c>char(2)</c> column against a 3-char parameter, rather than silently truncating or padding it
/// to fit and matching an unrelated 2-char row (here, Namibia's <c>NA</c>).
///
/// <para>
/// <b>Why this needs real PostgreSQL and cannot run on InMemory.</b> EF Core's InMemory provider
/// has no column types at all — a <c>char(2)</c> constraint exists only in the real Npgsql-backed
/// schema, so whatever Postgres/Npgsql actually does with an oversized parameter against a
/// fixed-length column can only be observed against a real database. The API test suite
/// (<c>AdminVerifyEndpointTests</c>, InMemory-backed) proves the route shape and canonicalisation;
/// this proves what the database itself does with the resulting key.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class EfVerifiableRepositoryPostgresTests : PostgresTestBase
{
    public EfVerifiableRepositoryPostgresTests(PostgresContainerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task FindByKeyAsync_ThreeCharKeyAgainstATwoCharCharColumn_DoesNotMatchTheTwoCharRow()
    {
        await using (var seed = CreateDbContext())
        {
            seed.Countries.Add(Country.Create("NA", "Namibia"));
            await seed.SaveChangesAsync();
        }

        // A fresh DbContext, distinct from the one used to seed — FindByKeyAsync must resolve
        // "NA" via a real SQL query against PostgreSQL, not via the seeding context's own
        // change tracker (which would trivially return the just-added in-memory entity without
        // ever exercising char(2) equality on the server).
        await using var db = CreateDbContext();
        var repo = new EfVerifiableRepository<Country>(db);

        var threeChar = await repo.FindByKeyAsync("NAN");
        Assert.Null(threeChar);

        var twoChar = await repo.FindByKeyAsync("NA");
        Assert.NotNull(twoChar);
        Assert.Equal("NA", twoChar!.Code);
    }
}
