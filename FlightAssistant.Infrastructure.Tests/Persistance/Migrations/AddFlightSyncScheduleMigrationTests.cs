using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Migrations;

/// <summary>
/// The <c>AddFlightSyncSchedule</c> backfill (#356): every tracked flight becomes due at once, so the
/// first loop plans it; untracked flights stay unplanned. Runs in a database of its own on the shared
/// container, migrated only to the migration before this one, so rows exist when the backfill runs.
/// The shared, fully migrated database isn't touched.
/// </summary>
/// <remarks>
/// Derives from <see cref="PostgresTestBase"/> to satisfy <c>PostgresCollectionMembershipTests</c>'s #310
/// guard. The base's reset of the shared database before each test is harmless here, so it's inherited;
/// <see cref="DisposeAsync"/> is overridden to drop this test's scratch database, which the base would
/// leave behind.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AddFlightSyncScheduleMigrationTests : PostgresTestBase
{
    private const string MigrationSuffix = "_AddFlightSyncSchedule";

    private readonly PostgresContainerFixture _fixture;
    private readonly string _databaseName = "backfill_" + Guid.NewGuid().ToString("N");
    private readonly string _connectionString;

    public AddFlightSyncScheduleMigrationTests(PostgresContainerFixture fixture) : base(fixture)
    {
        _fixture = fixture;
        _connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = _databaseName }.ConnectionString;
    }

    public override async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using (var db = _fixture.CreateDbContext())
        {
            var drop = "DROP DATABASE IF EXISTS \"" + _databaseName + "\" WITH (FORCE)";
            await db.Database.ExecuteSqlRawAsync(drop);
        }

        await base.DisposeAsync();
    }

    // Public: EF materializes it for SqlQueryRaw.
    public sealed class FlightRow
    {
        public string FlightNumber { get; set; } = "";
        public DateTime? NextSyncDueUtc { get; set; }
        public string? SyncMode { get; set; }
    }

    [Fact]
    public async Task Up_MakesTrackedFlightsDueNow_AndLeavesUntrackedFlightsUnplanned()
    {
        await using var db = new FlightAssistantDbContext(
            new DbContextOptionsBuilder<FlightAssistantDbContext>().UseNpgsql(_connectionString).Options);

        // Found by name, not hardcoded, so a migration merged in before this one doesn't break the test.
        var migrations = db.Database.GetMigrations().ToList();
        var index = migrations.FindIndex(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        Assert.True(index > 0, "AddFlightSyncSchedule migration not found.");
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(migrations[index - 1]);

        // TK1 tracked once, TK2 twice, TK3 not at all. Foreign keys are off for the seed: the backfill
        // reads only flights and traveler_flights, and the parent tables' columns at this schema
        // version aren't this test's concern.
        const string seed = """
            SET session_replication_role = replica;
            INSERT INTO flights ("AirlineId", "FlightNumber", "DepartureAirportId", "ArrivalAirportId", "ServiceDateUtc", "ScheduledDepartureUtc", "ScheduledArrivalUtc", "CreatedUtc", "UpdatedUtc")
            VALUES (1, 'TK1', 1, 2, DATE '2026-09-20', TIMESTAMPTZ '2026-09-20 08:00Z', TIMESTAMPTZ '2026-09-20 11:00Z', now(), now()),
                   (1, 'TK2', 1, 2, DATE '2026-09-20', TIMESTAMPTZ '2026-09-20 08:00Z', TIMESTAMPTZ '2026-09-20 11:00Z', now(), now()),
                   (1, 'TK3', 1, 2, DATE '2026-09-20', TIMESTAMPTZ '2026-09-20 08:00Z', TIMESTAMPTZ '2026-09-20 11:00Z', now(), now());
            INSERT INTO traveler_flights ("Id", "TravelerId", "FlightId", "Notify", "CreatedUtc")
            SELECT gen_random_uuid(), gen_random_uuid(), "Id", true, now() FROM flights WHERE "FlightNumber" IN ('TK1', 'TK2')
            UNION ALL
            SELECT gen_random_uuid(), gen_random_uuid(), "Id", true, now() FROM flights WHERE "FlightNumber" = 'TK2';
            SET session_replication_role = origin;
            """;
        await db.Database.ExecuteSqlRawAsync(seed);

        var before = DateTime.UtcNow;
        await migrator.MigrateAsync(migrations[index]);
        var after = DateTime.UtcNow;

        var rows = (await db.Database
                .SqlQueryRaw<FlightRow>("SELECT \"FlightNumber\", \"NextSyncDueUtc\", \"SyncMode\" FROM flights")
                .ToListAsync())
            .ToDictionary(r => r.FlightNumber);

        // A minute's slack either side for clock drift between the container and the host.
        Assert.InRange(rows["TK1"].NextSyncDueUtc ?? DateTime.MinValue, before.AddMinutes(-1), after.AddMinutes(1));
        Assert.InRange(rows["TK2"].NextSyncDueUtc ?? DateTime.MinValue, before.AddMinutes(-1), after.AddMinutes(1));
        Assert.Null(rows["TK3"].NextSyncDueUtc);
        Assert.All(rows.Values, r => Assert.Null(r.SyncMode)); // the first loop plans them
    }
}
