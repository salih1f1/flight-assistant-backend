using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// The whole reason issue #295 exists: three pieces of real relational behaviour that
/// <c>UseInMemoryDatabase</c> cannot see at all, proven here against the same real PostgreSQL the
/// harness gives every other Postgres-collection test. Each test's doc comment records what the
/// same assertion does if pointed at InMemory instead — see <see cref="PostgresContainerFixture"/>
/// for the fixture these run against.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PostgresPersistenceBehaviorTests : PostgresTestBase
{
    public PostgresPersistenceBehaviorTests(PostgresContainerFixture fixture) : base(fixture) { }

    /// <summary>
    /// The Critical defect this issue exists for: <c>GET /admin/usage/aeroapi?fromUtc=2026-01-01</c>
    /// returned 500 against real Postgres because a bare date binds a <see cref="DateTime"/> with
    /// <see cref="DateTimeKind.Unspecified"/>, and Npgsql refuses to write that to a
    /// <c>timestamptz</c> column. The production code that actually hit this now normalises Kind
    /// before it reaches the database (and no longer even persists the query bound in that
    /// endpoint), so this targets the persistence behaviour directly — via
    /// <see cref="Traveler.CreatedUtc"/>, mapped <c>timestamptz</c> by <c>TravelerConfiguration</c>
    /// — rather than re-testing an endpoint that no longer has the bug.
    ///
    /// <para>
    /// The public API only ever produces <c>Kind=Utc</c> (<see cref="Traveler"/> sets
    /// <c>CreatedUtc</c> from <see cref="DateTime.UtcNow"/>), so an Unspecified value is written
    /// through the change tracker directly — simulating exactly what a naively-parsed bare date
    /// would hand EF.
    /// </para>
    ///
    /// <para>
    /// <b>Under InMemory (verified, not assumed):</b> this assertion fails — InMemory ignores
    /// <see cref="DateTimeKind"/> entirely and accepts the write with no error, which is precisely
    /// how this defect shipped invisible to a green suite.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_RejectsADateTimeWithUnspecifiedKind_OnATimestamptzColumn()
    {
        await using var db = CreateDbContext();
        var traveler = Traveler.Register($"{Guid.NewGuid():N}@test.local");
        db.Travelers.Add(traveler);

        db.Entry(traveler).Property(nameof(Traveler.CreatedUtc)).CurrentValue =
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        // Npgsql refuses the Kind before the query ever reaches the server, so the inner exception
        // is its own ArgumentException, not a PostgresException from the server — real message:
        // "Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time
        // zone', only UTC is supported."
        var inner = Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Contains("Kind=Unspecified", inner.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The second defect this issue exists for: a retention prune used
    /// <see cref="RelationalQueryableExtensions.ExecuteDeleteAsync{TSource}(IQueryable{TSource}, CancellationToken)"/>,
    /// which EF's InMemory provider cannot translate at all — the delete never executed in any
    /// test. <see cref="Country"/> is used here (no FK dependents) purely so the test needs no
    /// setup beyond the one row it deletes; nothing about the assertion is specific to countries.
    ///
    /// <para>
    /// <b>Under InMemory (verified, not assumed):</b> this throws
    /// <see cref="InvalidOperationException"/> — "The methods 'ExecuteDelete' and
    /// 'ExecuteDeleteAsync' are not supported by the current database provider" — rather than
    /// silently no-op'ing. Either way the row survives, so the InMemory version of this test would
    /// still fail (for a different reason than a real caller's bug would), which is itself proof
    /// InMemory cannot exercise this path: a genuine translation bug and "wrong provider entirely"
    /// are indistinguishable there.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ExecuteDeleteAsync_DeletesMatchingRows_AgainstARealRelationalProvider()
    {
        await using var db = CreateDbContext();
        db.Countries.Add(Country.Create("ZZ", "Test Country For #295"));
        await db.SaveChangesAsync();

        var deletedCount = await db.Countries.Where(c => c.Code == "ZZ").ExecuteDeleteAsync();

        Assert.Equal(1, deletedCount);
        Assert.False(await db.Countries.AnyAsync(c => c.Code == "ZZ"));
    }

    /// <summary>
    /// <see cref="TravelerConfiguration"/> declares <c>Email</c> as <c>HasMaxLength(254)</c> — a
    /// real database enforces that as a <c>varchar(254)</c> column constraint; nothing in the
    /// domain factory itself checks length (see <see cref="Traveler"/>'s private constructor).
    ///
    /// <para>
    /// <b>Under InMemory (verified, not assumed):</b> this assertion fails — InMemory ignores
    /// <c>HasMaxLength</c> at runtime entirely (it is metadata EF never enforces client-side) and
    /// inserts the over-length value with no error.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_RejectsAStringLongerThanItsConfiguredColumnLength()
    {
        await using var db = CreateDbContext();
        var overLongEmail = new string('a', 300) + "@example.com"; // column is varchar(254)
        db.Travelers.Add(Traveler.Register(overLongEmail));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, pgEx.SqlState);
    }
}
