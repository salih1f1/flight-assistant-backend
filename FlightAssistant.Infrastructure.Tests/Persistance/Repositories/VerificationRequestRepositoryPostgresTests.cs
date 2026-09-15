using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Configurations.Verification;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace FlightAssistant.Infrastructure.Tests.Persistance.Repositories;

/// <summary>
/// <see cref="VerificationRequestRepository"/> against real PostgreSQL (#367 Task 1). Everything
/// here is PostgreSQL-only behaviour InMemory cannot show: the <c>FOR UPDATE SKIP LOCKED</c> claim
/// statement, the partial unique index, <c>jsonb</c> columns and <c>timestamptz</c> kinds.
/// </summary>
[Collection(PostgresCollection.Name)]
public class VerificationRequestRepositoryPostgresTests : PostgresTestBase
{
    private static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] OurAirports = { "ourairports" };

    private readonly PostgresContainerFixture _fixture;

    public VerificationRequestRepositoryPostgresTests(PostgresContainerFixture fixture) : base(fixture)
    {
        _fixture = fixture;
    }

    // ── Claiming ────────────────────────────────────────────────────────────

    /// <summary>
    /// Two claimers on separate connections. The first holds its claim in an open transaction, so
    /// its two rows stay locked while the second claims. With <c>SKIP LOCKED</c> the second passes
    /// over them and takes only the third row. Without it (or without <c>FOR UPDATE</c>, where the
    /// outer <c>UPDATE</c> would wait on the same row locks), the second claim waits on the first
    /// transaction; <c>lock_timeout</c> turns that wait into an immediate, deterministic failure
    /// instead of a hang.
    /// </summary>
    [Fact]
    public async Task ClaimBatchAsync_WhileAnotherConnectionHoldsItsClaimOpen_NeverReturnsTheSameRequest()
    {
        var ids = await SeedEachAsync(
            Request("1", requestedUtc: T0),
            Request("2", requestedUtc: T0.AddSeconds(1)),
            Request("3", requestedUtc: T0.AddSeconds(2)));
        var now = T0.AddMinutes(1);

        await using var first = CreateDbContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        var firstClaim = await Repository(first).ClaimBatchAsync(2, now);

        await using var second = CreateDbContext();
        await using var secondTransaction = await second.Database.BeginTransactionAsync();
        await second.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'");
        var secondClaim = await Repository(second).ClaimBatchAsync(10, now);

        // The premise: two different server sessions, not one connection reused.
        Assert.NotEqual(await BackendPidAsync(first), await BackendPidAsync(second));

        Assert.Equal(new[] { ids[0], ids[1] }, firstClaim.Select(r => r.Id));
        Assert.Equal(new[] { ids[2] }, secondClaim.Select(r => r.Id));

        await secondTransaction.CommitAsync();
        await firstTransaction.CommitAsync();

        await using var verify = CreateDbContext();
        Assert.All(await verify.VerificationRequests.ToListAsync(), r => Assert.Equal(VerificationRequestStatus.Running, r.Status));
    }

    /// <summary>
    /// Inserted so that neither id order nor <c>requested_utc</c> order alone produces the expected
    /// sequence. Each claim takes one row, so the sequence is decided by the SQL's
    /// <c>ORDER BY … LIMIT</c>, not by the in-memory sort of the returned batch.
    /// </summary>
    [Fact]
    public async Task ClaimBatchAsync_ClaimsFlightTriggeredRequestsFirst_ThenTheOldestRequested()
    {
        await SeedEachAsync(
            Request("admin-new", VerificationTrigger.Admin, T0.AddMinutes(1)),
            Request("flight-new", VerificationTrigger.Flight, T0.AddMinutes(2)),
            Request("admin-old", VerificationTrigger.Admin, T0),
            Request("flight-old", VerificationTrigger.Flight, T0.AddMinutes(1)));

        var claimOrder = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            await using var db = CreateDbContext();
            var claimed = await Repository(db).ClaimBatchAsync(1, T0.AddHours(1));
            claimOrder.Add(Assert.Single(claimed).EntityKey);
        }

        Assert.Equal(new[] { "flight-old", "flight-new", "admin-old", "admin-new" }, claimOrder);
    }

    [Fact]
    public async Task ClaimBatchAsync_ReturnsTheBatchInClaimOrder_AndMarksItRunning()
    {
        await SeedEachAsync(
            Request("admin-new", VerificationTrigger.Admin, T0.AddMinutes(1)),
            Request("flight-new", VerificationTrigger.Flight, T0.AddMinutes(2)),
            Request("admin-old", VerificationTrigger.Admin, T0),
            Request("flight-old", VerificationTrigger.Flight, T0.AddMinutes(1)));
        var now = T0.AddHours(1);

        await using var db = CreateDbContext();
        var claimed = await Repository(db).ClaimBatchAsync(10, now);

        Assert.Equal(new[] { "flight-old", "flight-new", "admin-old", "admin-new" }, claimed.Select(r => r.EntityKey));
        Assert.All(claimed, r =>
        {
            Assert.Equal(VerificationRequestStatus.Running, r.Status);
            Assert.Equal(now, r.StartedUtc);
        });
    }

    /// <summary><c>next_attempt_utc</c> in the future is not due; null, past and exactly-now are.</summary>
    [Fact]
    public async Task ClaimBatchAsync_SkipsARequestWhoseNextAttemptIsInTheFuture()
    {
        var now = T0.AddHours(1);
        var ids = await SeedEachAsync(Request("due-null"), Request("due-past"), Request("due-now"), Request("not-due"));

        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            (await repository.GetByIdAsync(ids[1]))!.RecordSourceError("timeout", now.AddMinutes(-6), 3, TimeSpan.FromMinutes(5)); // due now − 1 min
            (await repository.GetByIdAsync(ids[2]))!.RecordSourceError("timeout", now.AddMinutes(-5), 3, TimeSpan.FromMinutes(5)); // due exactly now
            (await repository.GetByIdAsync(ids[3]))!.RecordSourceError("timeout", now.AddMinutes(-1), 3, TimeSpan.FromMinutes(5)); // due now + 4 min
            await repository.SaveChangesAsync();
        }

        await using (var db = CreateDbContext())
        {
            var claimed = await Repository(db).ClaimBatchAsync(10, now);
            Assert.Equal(new[] { "due-now", "due-null", "due-past" }, claimed.Select(r => r.EntityKey).Order());
        }

        await using (var db = CreateDbContext())
        {
            var claimed = await Repository(db).ClaimBatchAsync(10, now.AddMinutes(4));
            Assert.Equal("not-due", Assert.Single(claimed).EntityKey);
        }
    }

    // ── Resetting stuck requests ────────────────────────────────────────────

    [Fact]
    public async Task ResetStuckRunningAsync_ResetsRunningOlderThanTenMinutes_AndLeavesYoungerAlone()
    {
        var now = T0.AddHours(1);
        var ids = await SeedEachAsync(
            Request("stuck", requestedUtc: T0),
            Request("young", requestedUtc: T0.AddSeconds(1)),
            Request("waiting", requestedUtc: T0.AddSeconds(2)));

        // "stuck" fails once first, so the reset can be seen to keep Attempts.
        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Equal(ids[0], Assert.Single(await repository.ClaimBatchAsync(1, now.AddMinutes(-30))).Id);
            (await repository.GetByIdAsync(ids[0]))!.RecordSourceError("timeout", now.AddMinutes(-30), 3, TimeSpan.Zero);
            await repository.SaveChangesAsync();
        }
        await using (var db = CreateDbContext())
            Assert.Equal(ids[0], Assert.Single(await Repository(db).ClaimBatchAsync(1, now.AddMinutes(-11))).Id);
        await using (var db = CreateDbContext())
            Assert.Equal(ids[1], Assert.Single(await Repository(db).ClaimBatchAsync(1, now.AddMinutes(-9))).Id);

        int reset;
        await using (var db = CreateDbContext())
            reset = await Repository(db).ResetStuckRunningAsync(now - TimeSpan.FromMinutes(10));

        Assert.Equal(1, reset);
        await using var verify = CreateDbContext();
        var rows = await verify.VerificationRequests.ToDictionaryAsync(r => r.EntityKey);

        Assert.Equal(VerificationRequestStatus.Pending, rows["stuck"].Status);
        Assert.Null(rows["stuck"].StartedUtc);
        Assert.Equal(1, rows["stuck"].Attempts);

        Assert.Equal(VerificationRequestStatus.Running, rows["young"].Status);
        Assert.Equal(now.AddMinutes(-9), rows["young"].StartedUtc);

        Assert.Equal(VerificationRequestStatus.Pending, rows["waiting"].Status);
        Assert.Null(rows["waiting"].StartedUtc);
    }

    // ── The open-request index and enqueueing ───────────────────────────────

    [Fact]
    public async Task OpenRequestIndex_RejectsASecondPendingRequestForTheSameEntity()
    {
        await SeedEachAsync(Request("42"));

        await using var db = CreateDbContext();
        db.VerificationRequests.Add(Request("42"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal(VerificationRequestConfiguration.OpenRequestIndexName, postgres.ConstraintName);
    }

    [Fact]
    public async Task EnqueueAsync_KeysWithAPendingOrRunningRequest_AreSkipped_AndTheRestQueued()
    {
        var ids = await SeedEachAsync(
            Request("running", requestedUtc: T0.AddMinutes(-1)),
            Request("pending", requestedUtc: T0),
            // Same key, different entity type: not a conflict.
            Request("new", requestedUtc: T0, entityType: VerificationEntityType.Country));
        await using (var db = CreateDbContext())
            Assert.Equal(ids[0], Assert.Single(await Repository(db).ClaimBatchAsync(1, T0)).Id);

        VerificationEnqueueResult result;
        await using (var db = CreateDbContext())
        {
            result = await Repository(db, now: T0.AddMinutes(5)).EnqueueAsync(
                VerificationEntityType.Airport, new[] { "running", "pending", "new", "new" }, OurAirports,
                VerificationTrigger.Admin, "admin-oid", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 3), result);

        await using var verify = CreateDbContext();
        var airports = await verify.VerificationRequests.Where(r => r.EntityType == VerificationEntityType.Airport).ToListAsync();
        Assert.Equal(3, airports.Count);
        var queued = Assert.Single(airports, r => r.EntityKey == "new");
        Assert.Equal(VerificationRequestStatus.Pending, queued.Status);
        Assert.Equal("admin-oid", queued.RequestedBy);
        Assert.Equal("ourairports", queued.Sources);
        Assert.Equal(T0.AddMinutes(5), queued.RequestedUtc);
    }

    /// <summary>
    /// The race: the other enqueue's insert lands after this call's check found no open request,
    /// so this call's insert hits the unique index. That conflict is skipped, the other keys still
    /// queue, and nothing throws.
    /// </summary>
    [Fact]
    public async Task EnqueueAsync_WhenAConcurrentEnqueueInsertsTheSameEntityAfterTheCheck_CountsItAsSkipped()
    {
        var racer = new RunOnceAfterFirstSelectInterceptor(async () =>
        {
            await using var other = CreateDbContext();
            other.VerificationRequests.Add(Request("2", requestedBy: "racer"));
            await other.SaveChangesAsync();
        });

        VerificationEnqueueResult result;
        await using (var db = CreateDbContext(racer))
        {
            result = await Repository(db).EnqueueAsync(
                VerificationEntityType.Airport, new[] { "1", "2", "3" }, OurAirports,
                VerificationTrigger.Admin, "admin-oid", 0m);
        }

        Assert.True(racer.HasRun, "The concurrent insert never ran, so this test did not exercise the race.");
        Assert.Equal(new VerificationEnqueueResult(Queued: 2, Skipped: 1), result);

        await using var verify = CreateDbContext();
        var requestedBy = await verify.VerificationRequests.ToDictionaryAsync(r => r.EntityKey, r => r.RequestedBy);
        Assert.Equal(new Dictionary<string, string> { ["1"] = "admin-oid", ["2"] = "racer", ["3"] = "admin-oid" }, requestedBy);
    }

    /// <summary>The unique index is partial: only an open request blocks a new one.</summary>
    [Fact]
    public async Task EnqueueAsync_EntityWhoseEarlierRequestCompleted_QueuesANewOne()
    {
        var ids = await SeedEachAsync(Request("7"));
        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Single(await repository.ClaimBatchAsync(1, T0.AddMinutes(1))).MarkVerified(T0.AddMinutes(2));
            await repository.SaveChangesAsync();
        }

        VerificationEnqueueResult result;
        await using (var db = CreateDbContext())
        {
            result = await Repository(db).EnqueueAsync(
                VerificationEntityType.Airport, new[] { "7" }, OurAirports, VerificationTrigger.Admin, "admin-oid", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 0), result);

        await using var verify = CreateDbContext();
        var statuses = await verify.VerificationRequests.OrderBy(r => r.Id).Select(r => r.Status).ToListAsync();
        Assert.Equal(new[] { VerificationRequestStatus.Verified, VerificationRequestStatus.Pending }, statuses);
        Assert.Equal(ids[0], await verify.VerificationRequests.Where(r => r.Status == VerificationRequestStatus.Verified).Select(r => r.Id).SingleAsync());
    }

    // ── Queries ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CountPendingAsync_CountsPendingOnly_DueOrNot()
    {
        var ids = await SeedEachAsync(
            Request("running", requestedUtc: T0.AddMinutes(-1)),
            Request("pending", requestedUtc: T0),
            Request("pending-not-due", requestedUtc: T0),
            Request("verified", requestedUtc: T0));

        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Equal(ids[0], Assert.Single(await repository.ClaimBatchAsync(1, T0)).Id);
            (await repository.GetByIdAsync(ids[2]))!.RecordSourceError("timeout", T0, 3, TimeSpan.FromHours(1));
            (await repository.GetByIdAsync(ids[3]))!.MarkVerified(T0);
            await repository.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        Assert.Equal(2, await Repository(verify).CountPendingAsync());
    }

    [Fact]
    public async Task AnyOpenRequestUsesSourceAsync_MatchesWholeSourceNamesOnOpenRequestsOnly()
    {
        var ids = await SeedEachAsync(
            Request("open", sources: new[] { "ourairports", "aeroapi" }),
            Request("completed", sources: new[] { "mledoze" }));
        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            (await repository.GetByIdAsync(ids[1]))!.MarkError("Entity no longer exists", T0);
            await repository.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        var queryRepository = Repository(verify);

        Assert.True(await queryRepository.AnyOpenRequestUsesSourceAsync("ourairports"));
        Assert.True(await queryRepository.AnyOpenRequestUsesSourceAsync("aeroapi"));
        Assert.False(await queryRepository.AnyOpenRequestUsesSourceAsync("ourair"));
        Assert.False(await queryRepository.AnyOpenRequestUsesSourceAsync("airports,aero"));
        Assert.False(await queryRepository.AnyOpenRequestUsesSourceAsync("mledoze"));
    }

    // ── Storage ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DifferencesAndSourceValues_RoundTripAsJsonb()
    {
        const string differences = """[{"column":"Name","ours":"Heathrow","source":"London Heathrow Airport","sourceName":"ourairports"}]""";
        const string sourceValues = """{"ourairports":{"Name":"London Heathrow Airport","City":"London","Latitude":51.4706}}""";
        var ids = await SeedEachAsync(Request("9"));

        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Single(await repository.ClaimBatchAsync(1, T0.AddMinutes(1))).MarkMismatch(differences, sourceValues, T0.AddMinutes(2));
            await repository.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        var row = (await Repository(verify).GetByIdAsync(ids[0]))!;

        // jsonb normalises whitespace and key order, so compare as JSON, not as strings.
        Assert.Equal(VerificationRequestStatus.Mismatch, row.Status);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(differences), JsonNode.Parse(row.DifferencesJson!)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(sourceValues), JsonNode.Parse(row.SourceValuesJson!)));

        var columnTypes = await verify.Database
            .SqlQuery<string>($"""SELECT pg_typeof("differences")::text || ',' || pg_typeof("source_values")::text AS "Value" FROM "verification_requests" """)
            .SingleAsync();
        Assert.Equal("jsonb,jsonb", columnTypes);

        var firstColumn = await verify.Database
            .SqlQuery<string>($"""SELECT "differences" -> 0 ->> 'column' AS "Value" FROM "verification_requests" """)
            .SingleAsync();
        Assert.Equal("Name", firstColumn);
    }

    [Fact]
    public async Task Timestamps_ReadBackWithUtcKind_FromTheClaimAndFromAFreshContext()
    {
        var claimedAt = T0.AddMinutes(30);
        var ids = await SeedEachAsync(Request("retrying", requestedUtc: T0), Request("done", requestedUtc: T0.AddSeconds(1)));

        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            var claimed = await repository.ClaimBatchAsync(2, claimedAt);

            Assert.All(claimed, r =>
            {
                AssertUtc(r.Id == ids[0] ? T0 : T0.AddSeconds(1), r.RequestedUtc);
                AssertUtc(claimedAt, r.StartedUtc);
            });

            claimed.Single(r => r.Id == ids[0]).RecordSourceError("timeout", claimedAt, 3, TimeSpan.FromMinutes(1));
            claimed.Single(r => r.Id == ids[1]).MarkVerified(claimedAt.AddMinutes(1));
            await repository.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        var rows = await verify.VerificationRequests.AsNoTracking().ToDictionaryAsync(r => r.Id);

        AssertUtc(T0, rows[ids[0]].RequestedUtc);
        AssertUtc(claimedAt.AddMinutes(1), rows[ids[0]].NextAttemptUtc);

        AssertUtc(T0.AddSeconds(1), rows[ids[1]].RequestedUtc);
        AssertUtc(claimedAt, rows[ids[1]].StartedUtc);
        AssertUtc(claimedAt.AddMinutes(1), rows[ids[1]].CompletedUtc);
    }

    // ── Auditing ────────────────────────────────────────────────────────────

    /// <summary>
    /// The job's save under its system actor: the airport's <c>Verify</c> is audited, the request's
    /// own status change is not (the queue table is its own history). Both changes are in one
    /// <c>SaveChanges</c>, so the audited row proves the interceptor ran on that save.
    /// </summary>
    [Fact]
    public async Task SystemActorSave_AuditsTheAirportVerify_ButNotTheRequestStatusChange()
    {
        int airportId;
        await using (var seed = CreateDbContext())
        {
            seed.Countries.Add(Country.Create("GB", "United Kingdom"));
            var airport = Airport.Create("LHR", "EGLL", "Heathrow", "London", "GB");
            seed.Airports.Add(airport);
            await seed.SaveChangesAsync();
            airportId = airport.Id;
        }
        await SeedEachAsync(Request(airportId.ToString(CultureInfo.InvariantCulture)));

        var jobActor = new AdminContext { ActorId = "system", ActorName = "Verification job" };
        await using (var db = CreateDbContext(new AdminAuditInterceptor(jobActor)))
        {
            var repository = Repository(db);
            var request = Assert.Single(await repository.ClaimBatchAsync(10, T0.AddMinutes(1)));
            var airport = await db.Airports.SingleAsync(a => a.Id == airportId);

            airport.Verify("system", "ourairports");
            request.MarkVerified(T0.AddMinutes(2));
            await repository.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        Assert.Equal(VerificationRequestStatus.Verified, (await verify.VerificationRequests.SingleAsync()).Status);

        var audit = Assert.Single(await verify.AdminAuditLogs.ToListAsync());
        Assert.Equal(AdminAuditAction.Verify, audit.Action);
        Assert.Equal(nameof(Airport), audit.EntityType);
        Assert.Equal(airportId.ToString(CultureInfo.InvariantCulture), audit.EntityId);
        Assert.Equal("system", audit.ActorId);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static VerificationRequestRepository Repository(FlightAssistantDbContext db, DateTime? now = null) =>
        new(db, new FakeTimeProvider(new DateTimeOffset(now ?? T0)));

    private static VerificationRequest Request(
        string key,
        VerificationTrigger trigger = VerificationTrigger.Admin,
        DateTime? requestedUtc = null,
        VerificationEntityType entityType = VerificationEntityType.Airport,
        string[]? sources = null,
        string requestedBy = "admin-oid") =>
        VerificationRequest.Create(entityType, key, sources ?? OurAirports, trigger, requestedBy, 0m, requestedUtc ?? T0);

    /// <summary>Saves each request in its own <c>SaveChanges</c>, so ids follow argument order.</summary>
    private async Task<long[]> SeedEachAsync(params VerificationRequest[] requests)
    {
        await using var db = CreateDbContext();
        foreach (var request in requests)
        {
            db.VerificationRequests.Add(request);
            await db.SaveChangesAsync();
        }
        return requests.Select(r => r.Id).ToArray();
    }

    private FlightAssistantDbContext CreateDbContext(params IInterceptor[] interceptors) =>
        interceptors.Length == 0
            ? base.CreateDbContext()
            : new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
                .UseNpgsql(_fixture.ConnectionString)
                .AddInterceptors(interceptors)
                .Options);

    private static Task<int> BackendPidAsync(FlightAssistantDbContext db) =>
        db.Database.SqlQuery<int>($"""SELECT pg_backend_pid() AS "Value" """).SingleAsync();

    private static void AssertUtc(DateTime expected, DateTime? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value);
        Assert.Equal(DateTimeKind.Utc, actual.Value.Kind);
    }

    /// <summary>Runs <paramref name="action"/> once, straight after the first SELECT on the context
    /// executes: for the enqueue, that is its open-request check, before its insert.</summary>
    private sealed class RunOnceAfterFirstSelectInterceptor(Func<Task> action) : DbCommandInterceptor
    {
        public bool HasRun { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!HasRun && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                HasRun = true;
                await action();
            }
            return result;
        }
    }
}
