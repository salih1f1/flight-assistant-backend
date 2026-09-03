using System.Text.Json;
using FlightAssistant.Application.Administration;
using FlightAssistant.Domain.Administration.Entities;
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

            // "No row persisted" alone doesn't discriminate a pre-commit interceptor (rolled back
            // together with the failed write) from a post-commit one that simply never ran — both
            // would leave zero persisted rows. This asserts the audit row was actually built and
            // staged as Added *before* the failed commit, which only the pre-commit design can
            // produce (#313 Task 2 review, F2/I2: a SavedChangesAsync/post-commit mutation sees
            // only Unchanged entries here — nothing pending, because it never got to add anything
            // before SaveChanges had already failed and unwound).
            var pending = db.ChangeTracker.Entries<AdminAuditLog>().ToList();
            Assert.Single(pending);
            Assert.Equal(EntityState.Added, pending[0].State);
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
    /// A store-generated key (<c>Airport.Id</c> is a Postgres identity column) genuinely does not
    /// exist yet at <c>SavingChanges</c> time — the interceptor runs before the INSERT that
    /// assigns it, and EF itself only holds a temporary placeholder for relationship fixup at that
    /// point (<c>PropertyEntry.IsTemporary</c>). <c>EntityId</c> records <c>null</c> for exactly
    /// this case rather than that placeholder or the CLR default (e.g. <c>"0"</c>): either of those
    /// would look like real data and collide across every create of every store-generated-key
    /// type, silently corrupting any future filter on <c>EntityId</c> (see
    /// <c>AdminAuditInterceptor.GetEntityId</c> and <c>AdminAuditLog.EntityId</c> for the full
    /// reasoning — #313 Task 2 review, F5/M5). The created row is still fully identifiable through
    /// <c>After</c>, asserted below.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_AddedEntityWithStoreGeneratedKey_RecordsNullEntityId_NotThePlaceholderOrTheRealId()
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
        // Pinned to null, not merely "not the real id" — a temporary placeholder value would also
        // satisfy NotEqual but would be exactly the collision-prone hazard this design avoids.
        Assert.Null(row.EntityId);
        Assert.NotEqual(realId.ToString(), row.EntityId);
        // The created row is still identifiable through After even though EntityId is unknown.
        Assert.NotNull(row.After);
        using var after = JsonDocument.Parse(row.After);
        Assert.Equal("ZZZ", after.RootElement.GetProperty(nameof(Airport.Iata)).GetString());
    }

    /// <summary>
    /// #318 (the audited reveal): a reveal is a read, so the only thing that ever touches this
    /// context's change tracker is the single <c>AdminAuditLog</c> row
    /// <c>RevealValueCommandHandler</c> builds and stages explicitly — nothing else is ever
    /// <c>Added</c>/<c>Modified</c>/<c>Deleted</c> in that save. This proves the interceptor's two
    /// documented guarantees actually hold against a real <c>SaveChangesAsync</c>, not just by
    /// reading the code: it does not try to audit an <c>AdminAuditLog</c> entity a second time
    /// (exactly one row persists, not two — <c>SingleAsync</c> below throws if a double-audit ever
    /// crept back in), and the append-only guard — which rejects <c>Modified</c>/<c>Deleted</c> —
    /// does not also reject a plain <c>Added</c> row.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_RevealRowAddedDirectly_PersistsExactlyOneRow_NeverDoubleAudited()
    {
        var adminContext = new AdminContext { ActorId = "admin-oid-1", ActorName = "Ada Admin" };
        var entityId = Guid.NewGuid().ToString();
        var revealRow = AdminAuditLog.Create(
            actorId: "admin-oid-1",
            actorName: "Ada Admin",
            action: AdminAuditAction.Reveal,
            entityType: nameof(Traveler),
            entityId: entityId,
            summary: $"Revealed {nameof(Traveler.Email)} on {nameof(Traveler)} {entityId}");

        await using (var db = CreateAuditedDbContext(adminContext))
        {
            db.AdminAuditLogs.Add(revealRow);
            await db.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        // SingleAsync, not First/Any: this is what actually fails if the interceptor ever audited
        // its own AdminAuditLog entry a second time — a plain count assertion would also catch it,
        // but this is the same idiom the rest of this file already uses.
        var row = await verify.AdminAuditLogs.SingleAsync();

        Assert.Equal(AdminAuditAction.Reveal, row.Action);
        Assert.Equal("admin-oid-1", row.ActorId);
        Assert.Equal(nameof(Traveler), row.EntityType);
        Assert.Equal(entityId, row.EntityId);
        Assert.Null(row.Before);
        Assert.Null(row.After);
    }

    /// <summary>
    /// #322 (the admin device-token delete): proves the "audited for free" claim end-to-end.
    /// <c>DeleteDeviceTokenCommandHandler</c> never builds an <see cref="AdminAuditLog"/> itself —
    /// it just calls <c>Traveler.RemoveDeviceToken</c> on a tracked, loaded aggregate and saves.
    /// <c>DeviceTokenConfiguration</c> configures <c>DeviceToken</c>'s FK to
    /// <c>Traveler</c> as <c>OnDelete(DeleteBehavior.Cascade)</c>, so removing a token from the
    /// loaded, tracked <c>DeviceTokens</c> collection marks that <c>DeviceToken</c> entry
    /// <see cref="EntityState.Deleted"/> — this is what <see cref="AdminAuditInterceptor"/> actually
    /// audits, not the <c>Traveler</c> the caller loaded.
    ///
    /// <para>
    /// <b>Why this has to run here, not through the API.</b>
    /// <c>FlightAssistantWebApplicationFactory</c> (see #319) strips
    /// <see cref="AdminAuditInterceptor"/> out of the <c>DbContext</c> registration entirely so the
    /// rest of the API test suite gets a fast, isolated InMemory database — which means an
    /// API-level integration test for this exact requirement could only ever observe "no audit row
    /// was written", the opposite of what #322 requires. This test drives the real interceptor
    /// against a real transaction instead, mirroring every other test in this file.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_DeviceTokenRemovedFromATrackedTraveler_WritesADeleteAuditRow_ForTheDeviceTokenNotTheTraveler()
    {
        var adminContext = new AdminContext { ActorId = "admin-oid-1", ActorName = "Ada Admin" };
        var traveler = Traveler.Register($"{Guid.NewGuid():N}@example.com");
        var token = traveler.AddDeviceToken("ios", $"raw-token-{Guid.NewGuid():N}");

        // Seed through an un-audited context — only the delete below should produce a row.
        await using (var seed = CreateAuditedDbContext(new AdminContext()))
        {
            seed.Travelers.Add(traveler);
            await seed.SaveChangesAsync();
        }

        await using (var db = CreateAuditedDbContext(adminContext))
        {
            var loaded = await db.Travelers.Include(t => t.DeviceTokens).SingleAsync(t => t.Id == traveler.Id);
            loaded.RemoveDeviceToken(token.Id);
            await db.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        var rows = await verify.AdminAuditLogs.ToListAsync();

        // Two entries actually change in that one SaveChangesAsync call: the removed DeviceToken
        // (Deleted) and the parent Traveler itself (Modified — RemoveDeviceToken calls SetUpdated(),
        // which touches UpdatedUtc). Each gets its own row, so this asserts both rather than
        // over-narrowing to Single() and failing for the wrong reason if the parent's own Update
        // row is ever the one that regresses.
        Assert.Equal(2, rows.Count);

        var deviceTokenRow = Assert.Single(rows, r => r.EntityType == nameof(DeviceToken));
        Assert.Equal(AdminAuditAction.Delete, deviceTokenRow.Action);
        Assert.Equal(token.Id.ToString(), deviceTokenRow.EntityId);
        Assert.Equal("admin-oid-1", deviceTokenRow.ActorId);

        var travelerRow = Assert.Single(rows, r => r.EntityType == nameof(Traveler));
        Assert.Equal(AdminAuditAction.Update, travelerRow.Action);
        Assert.Equal(traveler.Id.ToString(), travelerRow.EntityId);
    }
}
