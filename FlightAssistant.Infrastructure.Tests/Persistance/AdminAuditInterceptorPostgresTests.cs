using System.Text.Json;
using FlightAssistant.Application.Administration;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// The two things <see cref="AdminAuditInterceptorTests"/> (InMemory) cannot see at all: a real
/// transaction rolling an audit row back together with the write that failed, and a real
/// <c>jsonb</c> column round-tripping <c>Before</c>/<c>After</c>. Both need actual PostgreSQL —
/// see <see cref="PostgresContainerFixture"/> for why InMemory has neither.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AdminAuditInterceptorPostgresTests : PostgresTestBase
{
    private readonly PostgresContainerFixture _fixture;

    public AdminAuditInterceptorPostgresTests(PostgresContainerFixture fixture) : base(fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A context wired with <see cref="AdminAuditInterceptor"/> against the shared container.
    /// <see cref="PostgresTestBase.CreateDbContext"/> itself carries no interceptor (it is shared
    /// by every Postgres-collection test, most of which have nothing to do with auditing), so
    /// audited reads/writes build their own options here instead of going through that helper.
    /// </summary>
    private FlightAssistantDbContext CreateAuditedDbContext(AdminContext adminContext) =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(new AdminAuditInterceptor(adminContext))
            .Options);

    /// <summary>
    /// The transactional guarantee this whole design exists for: a <c>SaveChangesAsync</c> that
    /// the database rejects must leave no <c>admin_audit_log</c> row behind, even though the audit
    /// row was added to the very same <c>SaveChangesAsync</c> call as the failing write. Reuses the
    /// over-length-email trigger <see cref="PostgresPersistenceBehaviorTests"/> already proved
    /// forces a real Postgres <c>StringDataRightTruncation</c> — <c>Traveler.Email</c> is
    /// <c>varchar(254)</c>.
    ///
    /// <para>
    /// <b>Why InMemory can't prove this:</b> InMemory has no transactions and doesn't enforce
    /// <c>HasMaxLength</c> at all, so both the failure this test forces and the rollback it asserts
    /// on are simply invisible to it — the write would silently succeed either way.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_WhenTheUnderlyingWriteFails_LeavesNoAuditRow()
    {
        var adminContext = new AdminContext { ActorId = "admin-oid-1", ActorName = "Ada Admin" };

        await using (var db = CreateAuditedDbContext(adminContext))
        {
            var overLongEmail = new string('a', 300) + "@example.com"; // column is varchar(254)
            db.Travelers.Add(Traveler.Register(overLongEmail));

            // The AdminAuditLog row for this Traveler was added to the change tracker inside
            // SavingChangesAsync, in the same call — if it were a separate save, or fired only
            // after commit, this assertion alone couldn't tell "rolled back together" apart from
            // "never attempted".
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using var verify = CreateDbContext(); // plain read, no interceptor needed
        Assert.Empty(await verify.AdminAuditLogs.ToListAsync());
        Assert.Empty(await verify.Travelers.ToListAsync());
    }

    /// <summary>
    /// <c>Before</c>/<c>After</c> are mapped <c>jsonb</c> (see
    /// <c>AdminAuditLogConfiguration</c>) — this proves a real round-trip through Postgres, not
    /// just that <see cref="JsonSerializer"/> produced a syntactically valid string in memory.
    /// InMemory has no <c>jsonb</c> column type at all (every column is just a CLR object slot), so
    /// it cannot exercise the actual storage/read path this asserts on.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_PersistsAfterAsValidJsonb_AndRoundTrips()
    {
        var adminContext = new AdminContext { ActorId = "admin-oid-1", ActorName = "Ada Admin" };

        await using (var db = CreateAuditedDbContext(adminContext))
        {
            db.Countries.Add(Country.Create("ZZ", "Test Country For #313"));
            await db.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        var row = await verify.AdminAuditLogs.SingleAsync();

        Assert.Equal(AdminAuditAction.Create, row.Action);
        Assert.Null(row.Before);
        Assert.NotNull(row.After);

        // Round-tripped through a real jsonb column and back out as a string is still valid JSON,
        // and the values it carries are the ones that were actually written.
        using var doc = JsonDocument.Parse(row.After);
        Assert.Equal("ZZ", doc.RootElement.GetProperty(nameof(Country.Code)).GetString());
        Assert.Equal("Test Country For #313", doc.RootElement.GetProperty(nameof(Country.Name)).GetString());
    }

    /// <summary>
    /// <c>Before</c>/<c>After</c> also round-trip on the Modified path (Before = original values,
    /// After = current values, both restricted to only the properties that actually changed).
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_ModifiedRow_BeforeAndAfterBothRoundTripAsJsonb_RestrictedToChangedProperties()
    {
        var adminContext = new AdminContext { ActorId = "admin-oid-1", ActorName = "Ada Admin" };

        await using (var seed = CreateAuditedDbContext(new AdminContext())) // no actor: seed write isn't audited
        {
            seed.Countries.Add(Country.Create("YY", "Old Name"));
            await seed.SaveChangesAsync();
        }

        await using (var db = CreateAuditedDbContext(adminContext))
        {
            var country = await db.Countries.SingleAsync(c => c.Code == "YY");
            country.Update("New Name", null, null, null, null, null, null, null);
            await db.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        var row = await verify.AdminAuditLogs.SingleAsync(); // the seed save wrote none — no actor

        Assert.Equal(AdminAuditAction.Update, row.Action);
        Assert.NotNull(row.Before);
        Assert.NotNull(row.After);

        using var before = JsonDocument.Parse(row.Before);
        using var after = JsonDocument.Parse(row.After);

        Assert.Equal("Old Name", before.RootElement.GetProperty(nameof(Country.Name)).GetString());
        Assert.Equal("New Name", after.RootElement.GetProperty(nameof(Country.Name)).GetString());

        // Restricted to only the changed property — Code never changed, so it isn't carried at all.
        Assert.False(before.RootElement.TryGetProperty(nameof(Country.Code), out _));
        Assert.False(after.RootElement.TryGetProperty(nameof(Country.Code), out _));
    }

    /// <summary>
    /// A store-generated key (<c>Airport.Id</c> is a Postgres identity column) is still unset at
    /// <c>SavingChanges</c> time — the interceptor runs before the INSERT that assigns it, so it
    /// records whatever value the property actually holds at that point rather than the real id the
    /// row ends up with. This is the "record what you can, don't invent a value" case called out for
    /// <c>EntityId</c>; asserting the two differ documents the behaviour instead of leaving it
    /// implicit.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_AddedEntityWithStoreGeneratedKey_RecordsTheUnsetValue_NotTheRealId()
    {
        var adminContext = new AdminContext { ActorId = "admin-oid-1", ActorName = "Ada Admin" };
        var airport = Airport.Create("ZZZ", null, "Test Airport For #313", "Nowhere", "US");

        // Airports FK-reference their country — seed it through an un-audited context (no actor)
        // so the only audit row this test produces is the Airport's own.
        await using (var seed = CreateAuditedDbContext(new AdminContext()))
        {
            seed.Countries.Add(Country.Create("US", "United States"));
            await seed.SaveChangesAsync();
        }

        await using (var db = CreateAuditedDbContext(adminContext))
        {
            db.Airports.Add(airport);
            await db.SaveChangesAsync();
        }

        // EF updates the tracked entity's Id in place once Postgres assigns it, so this is the real,
        // final, database-generated id — captured only after SaveChangesAsync returned.
        var realId = airport.Id;

        await using var verify = CreateDbContext();
        var row = await verify.AdminAuditLogs.SingleAsync();

        Assert.Equal(nameof(Airport), row.EntityType);
        // The value recorded during SavingChanges (before Postgres assigned the identity value)
        // is not the real id — proving the interceptor captured whatever was present rather than
        // waiting for (or inventing) the generated one.
        Assert.NotEqual(realId.ToString(), row.EntityId);
    }
}
