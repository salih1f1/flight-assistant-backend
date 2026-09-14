using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// Everything about the <c>VerificationSource</c>/<c>VerificationIssue</c> columns (#355 Task 1)
/// that InMemory cannot see: a real round-trip through the actual <c>varchar</c> columns via a
/// fresh <see cref="FlightAssistantDbContext"/> (not the change tracker that wrote the values), and
/// the migration's backfill SQL actually running against real rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public class VerificationSourceAndIssuePostgresTests : PostgresTestBase
{
    /// <summary>The migration immediately before <c>AddVerificationSourceAndIssue</c> — the state
    /// the backfill test seeds against, before migrating back up runs the backfill SQL.
    ///
    /// <para>
    /// Migrating down to this pin also rolls back every migration <em>after</em> it — today that's
    /// only <c>AddVerificationSourceAndIssue</c> itself, but as new migrations land ahead of this
    /// one, their <c>Down</c> runs too. A later migration with a lossy or throwing <c>Down</c> would
    /// then fail this test, not because the backfill broke but because rolling back an unrelated,
    /// newer migration did. Once <c>AddVerificationSourceAndIssue</c> has actually run in every
    /// environment (so there is no longer a real "before the backfill" state to prove), this test —
    /// and the down/up dance it depends on — can simply be deleted rather than kept in sync with
    /// every migration added after it.
    /// </para>
    /// </summary>
    private const string PreviousMigration = "20260914180431_DropUnwrittenEntityFields";

    private readonly PostgresContainerFixture _fixture;

    public VerificationSourceAndIssuePostgresTests(PostgresContainerFixture fixture) : base(fixture)
    {
        _fixture = fixture;
    }

    // ── Round-trip: a fresh context, not the change tracker that wrote the values ──

    [Fact]
    public async Task VerificationSourceAndIssue_RoundTripThroughAirport_AFreshContextSeesWhatWasSaved()
    {
        int airportId;
        await using (var db = CreateDbContext())
        {
            db.Countries.Add(Country.Create("GB", "United Kingdom"));
            var airport = Airport.Create("ZVR", null, "Verification Round Trip Airport", "Testville", "GB");
            airport.MarkVerificationFailed("Name, City differ (ourairports)");
            db.Airports.Add(airport);
            await db.SaveChangesAsync();
            airportId = airport.Id;
        }

        await using var fresh = CreateDbContext();
        var reloaded = await fresh.Airports.AsNoTracking().SingleAsync(a => a.Id == airportId);

        Assert.False(reloaded.IsVerified);
        Assert.Null(reloaded.VerificationSource);
        Assert.Equal("Name, City differ (ourairports)", reloaded.VerificationIssue);
    }

    [Fact]
    public async Task VerificationSourceAndIssue_RoundTripThroughLoungeProvider_AFreshContextSeesWhatWasSaved()
    {
        int providerId;
        await using (var db = CreateDbContext())
        {
            var provider = LoungeProvider.Create("ZVP", "Verification Round Trip Provider", LoungeProviderKind.Contract);
            provider.Verify("admin@example.com", "ourairports+aeroapi");
            db.Set<LoungeProvider>().Add(provider);
            await db.SaveChangesAsync();
            providerId = provider.Id;
        }

        await using var fresh = CreateDbContext();
        var reloaded = await fresh.Set<LoungeProvider>().AsNoTracking().SingleAsync(p => p.Id == providerId);

        Assert.True(reloaded.IsVerified);
        Assert.Equal("ourairports+aeroapi", reloaded.VerificationSource);
        Assert.Null(reloaded.VerificationIssue);
    }

    // ── Backfill SQL ────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="PostgresContainerFixture"/> migrates the shared container to the latest migration
    /// once at container start, so there is no "before this migration ran" database to seed against
    /// directly. Instead this test migrates the live database back down one step (dropping just the
    /// two new columns via <c>AddVerificationSourceAndIssue</c>'s own <c>Down</c>), seeds rows with
    /// raw SQL against that pre-migration schema, then migrates forward again — which re-adds the
    /// columns and runs the real backfill <c>UPDATE</c> statements from the migration's <c>Up</c>.
    /// Safe to do against the shared container because Postgres-collection test classes run
    /// sequentially (see <see cref="PostgresContainerFixture"/>), and the test ends with the schema
    /// back at the latest migration for whatever runs next.
    /// </summary>
    [Fact]
    public async Task Migration_BackfillsManualSourceForAlreadyVerifiedRows_AndLeavesUnverifiedRowsNull()
    {
        await using var db = CreateDbContext();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigration);
        try
        {
            // Pre-migration schema: no verification_source/verification_issue columns yet, so this
            // has to be raw SQL rather than the EF model (which still declares those properties).
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO countries (\"Code\", \"Name\", \"SourceType\", \"FirstSeenAt\", is_verified) " +
                "VALUES ('XV', 'Backfill Verified Country', 'admin', now(), true);");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO countries (\"Code\", \"Name\", \"SourceType\", \"FirstSeenAt\", is_verified) " +
                "VALUES ('XW', 'Backfill Unverified Country', 'admin', now(), false);");
        }
        finally
        {
            // Always migrate back up, even if seeding threw, so a failed test doesn't leave the
            // shared container's schema behind for whatever test runs next. A null target
            // migrates to the latest one, which is exactly AddVerificationSourceAndIssue today.
            await migrator.MigrateAsync(targetMigration: null);
        }

        await using var fresh = CreateDbContext();
        var verified = await fresh.Countries.AsNoTracking().SingleAsync(c => c.Code == "XV");
        var unverified = await fresh.Countries.AsNoTracking().SingleAsync(c => c.Code == "XW");

        Assert.Equal(VerificationSources.Manual, verified.VerificationSource);
        Assert.Null(unverified.VerificationSource);
    }
}
