using FlightAssistant.Domain.DataSources.Entities;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// <see cref="DataSourceStatusRepository"/> against real PostgreSQL (#460 Task 5). Upsert is the
/// whole point of this repository — one row per catalog key, written over and over by the
/// observer's flush — so it is asserted against the real primary key rather than InMemory.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DataSourceStatusRepositoryPostgresTests : PostgresTestBase
{
    private static readonly DateTime T0 = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    public DataSourceStatusRepositoryPostgresTests(PostgresContainerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task UpsertAsync_InsertsARowThatDoesNotExist()
    {
        await using var write = CreateDbContext();
        await new DataSourceStatusRepository(write).UpsertAsync(new DataSourceStatus
        {
            Key = "aeroapi",
            LastOutcome = "Ok",
            LastOutcomeUtc = T0,
            LastOkUtc = T0,
        }, CancellationToken.None);

        // A fresh context, so this reads the database rather than the writer's change tracker.
        await using var read = CreateDbContext();
        var stored = await new DataSourceStatusRepository(read).GetAsync("aeroapi", CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal("Ok", stored!.LastOutcome);
        Assert.Equal(T0, stored.LastOkUtc);
    }

    [Fact]
    public async Task UpsertAsync_OverwritesARowThatAlreadyExists()
    {
        await using var db = CreateDbContext();
        var repo = new DataSourceStatusRepository(db);

        await repo.UpsertAsync(new DataSourceStatus { Key = "aeroapi", LastOutcome = "Ok", LastOutcomeUtc = T0 }, CancellationToken.None);
        await repo.UpsertAsync(new DataSourceStatus
        {
            Key = "aeroapi",
            LastOutcome = "Unauthorized",
            LastOutcomeUtc = T0.AddMinutes(1),
            LastError = "aeroapi returned HTTP 401",
            ConsecutiveFailures = 1,
        }, CancellationToken.None);

        await using var read = CreateDbContext();
        var readRepo = new DataSourceStatusRepository(read);
        var stored = await readRepo.GetAsync("aeroapi", CancellationToken.None);

        Assert.Equal("Unauthorized", stored!.LastOutcome);
        Assert.Equal(1, stored.ConsecutiveFailures);
        Assert.Equal("aeroapi returned HTTP 401", stored.LastError);

        var all = await readRepo.GetAllAsync(CancellationToken.None);
        Assert.Single(all, r => r.Key == "aeroapi");
    }

    /// <summary>
    /// The probe fields are written only by the probe path, so an observation flush — which carries
    /// none — must leave the last operator-triggered check where it was.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_LeavesTheProbeFields_WhenTheIncomingRowCarriesNone()
    {
        await using var db = CreateDbContext();
        var repo = new DataSourceStatusRepository(db);

        await repo.UpsertAsync(new DataSourceStatus
        {
            Key = "aeroapi",
            LastOutcome = "Ok",
            LastOutcomeUtc = T0,
            LastProbeUtc = T0,
            LastProbeBy = "admin-oid",
        }, CancellationToken.None);

        await repo.UpsertAsync(new DataSourceStatus { Key = "aeroapi", LastOutcome = "Failed", LastOutcomeUtc = T0.AddMinutes(5) }, CancellationToken.None);

        await using var read = CreateDbContext();
        var stored = await new DataSourceStatusRepository(read).GetAsync("aeroapi", CancellationToken.None);

        Assert.Equal(T0, stored!.LastProbeUtc);
        Assert.Equal("admin-oid", stored.LastProbeBy);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_ForAKeyThatWasNeverObserved()
    {
        await using var db = CreateDbContext();

        Assert.Null(await new DataSourceStatusRepository(db).GetAsync("never-seen", CancellationToken.None));
    }
}
