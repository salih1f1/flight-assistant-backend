using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Verification;
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
/// statement, the <c>xmin</c> concurrency token, the partial unique index, <c>jsonb</c> columns and
/// <c>timestamptz</c> kinds.
/// </summary>
[Collection(PostgresCollection.Name)]
public class VerificationRequestRepositoryPostgresTests : PostgresTestBase
{
    private static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] OurAirports = { "ourairports" };
    private static readonly string[] Mledoze = { "mledoze" };

    private const string Differences = """[{"column":"Name","ours":"Heathrow","source":"London Heathrow Airport","sourceName":"ourairports"}]""";
    private const string SourceValues = """{"ourairports":{"Name":"London Heathrow Airport","City":"London","Latitude":51.4706}}""";

    /// <summary>#418's default: matches <c>VerificationOptions.ErrorCooldown</c>'s own default.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(6);

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
    /// <c>UPDATE</c> would wait on the same row locks), the second claim waits on the first
    /// transaction; <c>lock_timeout</c> turns that wait into an immediate, deterministic failure
    /// instead of a hang. The first claim taking exactly 2 of 3 rows also pins "at most batchSize".
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

    private const string AdminNew = "1", FlightNew = "2", AdminOld = "3", FlightOld = "4";

    /// <summary>
    /// Inserted so that neither id order nor <c>requested_utc</c> order alone produces the expected
    /// sequence. Each claim takes one row, so the sequence is decided by the SQL's
    /// <c>ORDER BY … LIMIT</c>, not by the in-memory sort of the returned batch.
    /// </summary>
    [Fact]
    public async Task ClaimBatchAsync_ClaimsFlightTriggeredRequestsFirst_ThenTheOldestRequested()
    {
        await SeedOrderingScenarioAsync();

        var claimOrder = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            await using var db = CreateDbContext();
            var claimed = await Repository(db).ClaimBatchAsync(1, T0.AddHours(1));
            claimOrder.Add(Assert.Single(claimed).EntityKey);
        }

        Assert.Equal(new[] { FlightOld, FlightNew, AdminOld, AdminNew }, claimOrder);
    }

    [Fact]
    public async Task ClaimBatchAsync_ReturnsTheBatchInClaimOrder_AndMarksItRunning()
    {
        await SeedOrderingScenarioAsync();
        var now = T0.AddHours(1);

        await using var db = CreateDbContext();
        var claimed = await Repository(db).ClaimBatchAsync(10, now);

        Assert.Equal(new[] { FlightOld, FlightNew, AdminOld, AdminNew }, claimed.Select(r => r.EntityKey));
        Assert.All(claimed, r =>
        {
            Assert.Equal(VerificationRequestStatus.Running, r.Status);
            Assert.Equal(now, r.StartedUtc);
        });
    }

    /// <summary>
    /// #401 final fix G1: excludes the otherwise-first row by id, in real SQL -- proving
    /// <c>NOT ("id" = ANY(...))</c> actually reaches the query, not just that the C# threads the
    /// parameter through. The excluded row stays Pending, untouched: exclusion is not the same as
    /// shrinking the batch.
    /// </summary>
    [Fact]
    public async Task ClaimBatchAsync_ExcludesGivenIds_ClaimingTheNextEligibleRowInstead()
    {
        var ids = await SeedEachAsync(
            Request("1", requestedUtc: T0),
            Request("2", requestedUtc: T0.AddSeconds(1)),
            Request("3", requestedUtc: T0.AddSeconds(2)));
        var now = T0.AddMinutes(1);

        await using var db = CreateDbContext();
        var claimed = await Repository(db).ClaimBatchAsync(1, now, excludeIds: new[] { ids[0] });

        Assert.Equal(new[] { ids[1] }, claimed.Select(r => r.Id));

        await using var verify = CreateDbContext();
        var untouched = await verify.VerificationRequests.AsNoTracking().SingleAsync(r => r.Id == ids[0]);
        Assert.Equal(VerificationRequestStatus.Pending, untouched.Status);
    }

    /// <summary>#401 final fix G1's "don't loop" case at the SQL layer: excluding every otherwise-due
    /// id claims nothing rather than throwing or ignoring the exclusion.</summary>
    [Fact]
    public async Task ClaimBatchAsync_ExcludingEveryEligibleId_ClaimsNothing()
    {
        var ids = await SeedEachAsync(Request("1", requestedUtc: T0), Request("2", requestedUtc: T0.AddSeconds(1)));
        var now = T0.AddMinutes(1);

        await using var db = CreateDbContext();
        var claimed = await Repository(db).ClaimBatchAsync(10, now, excludeIds: ids);

        Assert.Empty(claimed);
    }

    /// <summary><c>next_attempt_utc</c> in the future is not due; null, past and exactly-now are.</summary>
    [Fact]
    public async Task ClaimBatchAsync_SkipsARequestWhoseNextAttemptIsInTheFuture()
    {
        const string dueNull = "1", duePast = "2", dueNow = "3", notDue = "4";
        var now = T0.AddHours(1);
        var ids = await SeedEachAsync(Request(dueNull), Request(duePast), Request(dueNow), Request(notDue));

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
            Assert.Equal(new[] { dueNull, duePast, dueNow }, claimed.Select(r => r.EntityKey).Order());
        }

        await using (var db = CreateDbContext())
        {
            var claimed = await Repository(db).ClaimBatchAsync(10, now.AddMinutes(4));
            Assert.Equal(notDue, Assert.Single(claimed).EntityKey);
        }
    }

    // ── Concurrency token: a claim lost to a stuck reset ────────────────────

    /// <summary>
    /// A claims; A runs so long that B resets the request as stuck and claims it again; A then saves
    /// an outcome. A's save must fail on the <c>xmin</c> token (as <see cref="VerificationClaimLostException"/>) instead of overwriting B's claim,
    /// and B's claim, and then B's own outcome, must be untouched by it.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_OutcomeFromAClaimLostToAStuckResetAndReclaim_ThrowsConcurrency_AndTheNewClaimStaysIntact()
    {
        await SeedEachAsync(Request("1"));
        var firstClaimAt = T0.AddMinutes(1);
        var secondClaimAt = T0.AddMinutes(20);

        await using var first = CreateDbContext();
        var firstRepository = Repository(first);
        var stale = Assert.Single(await firstRepository.ClaimBatchAsync(1, firstClaimAt));

        await using var second = CreateDbContext();
        var secondRepository = Repository(second);
        Assert.Equal(new VerificationStuckResetResult(1, 0), await secondRepository.ResetStuckRunningAsync(secondClaimAt - TimeSpan.FromMinutes(10), maxAttempts: 3, secondClaimAt));
        var current = Assert.Single(await secondRepository.ClaimBatchAsync(1, secondClaimAt));

        stale.MarkVerified(firstClaimAt.AddMinutes(15));
        var lost = await Assert.ThrowsAsync<VerificationClaimLostException>(() => firstRepository.SaveChangesAsync());
        Assert.IsType<DbUpdateConcurrencyException>(lost.InnerException);
        Assert.Equal(new[] { stale.Id }, lost.RequestIds);

        await using (var afterStaleSave = CreateDbContext())
        {
            var row = await afterStaleSave.VerificationRequests.AsNoTracking().SingleAsync();
            Assert.Equal(VerificationRequestStatus.Running, row.Status);
            Assert.Equal(secondClaimAt, row.StartedUtc);
            Assert.Null(row.CompletedUtc);
        }

        current.MarkMismatch(Differences, SourceValues, secondClaimAt.AddMinutes(1));
        await secondRepository.SaveChangesAsync();

        await using var verify = CreateDbContext();
        var saved = await verify.VerificationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(VerificationRequestStatus.Mismatch, saved.Status);
        Assert.Equal(secondClaimAt, saved.StartedUtc);
        Assert.Equal(secondClaimAt.AddMinutes(1), saved.CompletedUtc);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Differences), JsonNode.Parse(saved.DifferencesJson!)));
    }

    /// <summary>
    /// The other order, with the job's real save shape: B has already completed the request when A's
    /// stale save arrives carrying an airport <c>Verify()</c> under the system actor. The whole save
    /// rolls back, so the airport stays unverified, no audit row is written, and B's completed
    /// outcome is not flipped.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_StaleClaimCarryingAnEntityVerify_RollsBackTheVerifyAndItsAuditRow_AndKeepsTheNewerOutcome()
    {
        var airportId = await SeedAirportAsync();
        await SeedEachAsync(Request(airportId.ToString(CultureInfo.InvariantCulture)));
        var firstClaimAt = T0.AddMinutes(1);
        var secondClaimAt = T0.AddMinutes(20);
        var jobActor = new AdminContext { ActorId = "system", ActorName = "Verification job" };

        await using var first = CreateDbContext(new AdminAuditInterceptor(jobActor));
        var firstRepository = Repository(first);
        var stale = Assert.Single(await firstRepository.ClaimBatchAsync(1, firstClaimAt));

        await using (var second = CreateDbContext())
        {
            var secondRepository = Repository(second);
            Assert.Equal(new VerificationStuckResetResult(1, 0), await secondRepository.ResetStuckRunningAsync(secondClaimAt - TimeSpan.FromMinutes(10), maxAttempts: 3, secondClaimAt));
            Assert.Single(await secondRepository.ClaimBatchAsync(1, secondClaimAt))
                .MarkMismatch(Differences, SourceValues, secondClaimAt.AddMinutes(1));
            await secondRepository.SaveChangesAsync();
        }

        var airport = await first.Airports.SingleAsync(a => a.Id == airportId);
        airport.Verify("system", "ourairports");
        stale.MarkVerified(secondClaimAt.AddMinutes(2));
        var lost = await Assert.ThrowsAsync<VerificationClaimLostException>(() => firstRepository.SaveChangesAsync());
        Assert.IsType<DbUpdateConcurrencyException>(lost.InnerException);
        Assert.Equal(new[] { stale.Id }, lost.RequestIds);

        await using var verify = CreateDbContext();
        Assert.False((await verify.Airports.AsNoTracking().SingleAsync(a => a.Id == airportId)).IsVerified);
        Assert.Empty(await verify.AdminAuditLogs.ToListAsync());

        var row = await verify.VerificationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(VerificationRequestStatus.Mismatch, row.Status);
        Assert.Equal(secondClaimAt.AddMinutes(1), row.CompletedUtc);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Differences), JsonNode.Parse(row.DifferencesJson!)));
    }

    // ── Resetting stuck requests ────────────────────────────────────────────

    [Fact]
    public async Task ResetStuckRunningAsync_ResetsRunningOlderThanTenMinutes_AndLeavesYoungerAlone()
    {
        const string stuck = "1", young = "2", waiting = "3";
        var now = T0.AddHours(1);
        var ids = await SeedEachAsync(
            Request(stuck, requestedUtc: T0),
            Request(young, requestedUtc: T0.AddSeconds(1)),
            Request(waiting, requestedUtc: T0.AddSeconds(2)));

        // "stuck" fails once first, so the reset can be seen to add to Attempts, not restart them.
        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Single(await repository.ClaimBatchAsync(1, now.AddMinutes(-30)))
                .RecordSourceError("timeout", now.AddMinutes(-30), 3, TimeSpan.Zero);
            await repository.SaveChangesAsync();
        }
        await using (var db = CreateDbContext())
            Assert.Equal(ids[0], Assert.Single(await Repository(db).ClaimBatchAsync(1, now.AddMinutes(-11))).Id);
        await using (var db = CreateDbContext())
            Assert.Equal(ids[1], Assert.Single(await Repository(db).ClaimBatchAsync(1, now.AddMinutes(-9))).Id);

        VerificationStuckResetResult reset;
        await using (var db = CreateDbContext())
            reset = await Repository(db).ResetStuckRunningAsync(now - TimeSpan.FromMinutes(10), maxAttempts: 5, now);

        Assert.Equal(new VerificationStuckResetResult(ReturnedToPending: 1, Abandoned: 0), reset);
        await using var verify = CreateDbContext();
        var rows = await verify.VerificationRequests.ToDictionaryAsync(r => r.EntityKey);

        Assert.Equal(VerificationRequestStatus.Pending, rows[stuck].Status);
        Assert.Null(rows[stuck].StartedUtc);
        Assert.Equal(2, rows[stuck].Attempts); // the earlier source error, plus the abandoned claim
        Assert.Equal("timeout", rows[stuck].Error);
        Assert.Null(rows[stuck].CompletedUtc);

        Assert.Equal(VerificationRequestStatus.Running, rows[young].Status);
        Assert.Equal(now.AddMinutes(-9), rows[young].StartedUtc);

        Assert.Equal(VerificationRequestStatus.Pending, rows[waiting].Status);
        Assert.Null(rows[waiting].StartedUtc);
    }

    /// <summary>
    /// Both sides of the strict <c>started_utc &lt; cutoff</c>: started exactly at the cutoff is left
    /// Running; one microsecond earlier (PostgreSQL's <c>timestamptz</c> resolution) is reset.
    /// </summary>
    [Fact]
    public async Task ResetStuckRunningAsync_StartedExactlyAtTheCutoff_IsLeftAlone_OneMicrosecondEarlierIsReset()
    {
        const string justBefore = "1", exactlyAt = "2";
        var cutoff = T0.AddHours(1) - TimeSpan.FromMinutes(10);
        await SeedEachAsync(Request(justBefore, requestedUtc: T0), Request(exactlyAt, requestedUtc: T0.AddSeconds(1)));

        await using (var db = CreateDbContext())
            Assert.Equal(justBefore, Assert.Single(await Repository(db).ClaimBatchAsync(1, cutoff.AddTicks(-10))).EntityKey);
        await using (var db = CreateDbContext())
            Assert.Equal(exactlyAt, Assert.Single(await Repository(db).ClaimBatchAsync(1, cutoff)).EntityKey);

        VerificationStuckResetResult reset;
        await using (var db = CreateDbContext())
            reset = await Repository(db).ResetStuckRunningAsync(cutoff, maxAttempts: 3, cutoff + TimeSpan.FromMinutes(10));

        Assert.Equal(new VerificationStuckResetResult(ReturnedToPending: 1, Abandoned: 0), reset);
        await using var verify = CreateDbContext();
        var rows = await verify.VerificationRequests.ToDictionaryAsync(r => r.EntityKey);
        Assert.Equal(VerificationRequestStatus.Pending, rows[justBefore].Status);
        Assert.Equal(VerificationRequestStatus.Running, rows[exactlyAt].Status);
        Assert.Equal(cutoff, rows[exactlyAt].StartedUtc);
    }

    /// <summary>A stuck claim below <c>MaxAttempts</c> goes back to Pending with the abandoned claim
    /// counted as an attempt (fix round 1 ruling), rather than coming back for free.</summary>
    [Fact]
    public async Task ResetStuckRunningAsync_BelowMaxAttempts_ReturnsToPendingWithOneMoreAttempt()
    {
        var now = T0.AddHours(1);
        await SeedEachAsync(Request("1"));
        await using (var db = CreateDbContext())
            Assert.Single(await Repository(db).ClaimBatchAsync(1, now.AddMinutes(-11)));

        VerificationStuckResetResult reset;
        await using (var db = CreateDbContext())
            reset = await Repository(db).ResetStuckRunningAsync(now - TimeSpan.FromMinutes(10), maxAttempts: 3, now);

        Assert.Equal(new VerificationStuckResetResult(ReturnedToPending: 1, Abandoned: 0), reset);
        await using var verify = CreateDbContext();
        var row = await verify.VerificationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(VerificationRequestStatus.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.StartedUtc);
        Assert.Null(row.CompletedUtc);
        Assert.Null(row.Error);
        Assert.Null(row.NextAttemptUtc);
    }

    /// <summary>
    /// The reset whose added attempt reaches <c>MaxAttempts</c> completes the request as Error with
    /// the abandoned-claim message instead of returning it to Pending, so a request that never
    /// records a result stops coming back. An Error request is no longer open: never claimed again,
    /// not pending, and no longer holding its source.
    /// </summary>
    [Fact]
    public async Task ResetStuckRunningAsync_ReachingMaxAttempts_CompletesAsError_AndTheRequestIsNotReclaimable()
    {
        var now = T0.AddHours(1);
        await SeedEachAsync(Request("1"));

        // Two claims ended in recorded source errors, each due again at once...
        for (var i = 0; i < 2; i++)
        {
            var at = now.AddMinutes(-40 + i);
            await using var db = CreateDbContext();
            var repository = Repository(db);
            Assert.Single(await repository.ClaimBatchAsync(1, at))
                .RecordSourceError("HTTP 503", at, maxAttempts: 3, backoff: TimeSpan.Zero);
            await repository.SaveChangesAsync();
        }
        // ...and a third claim recorded nothing.
        await using (var db = CreateDbContext())
            Assert.Single(await Repository(db).ClaimBatchAsync(1, now.AddMinutes(-11)));

        VerificationStuckResetResult reset;
        await using (var db = CreateDbContext())
            reset = await Repository(db).ResetStuckRunningAsync(now - TimeSpan.FromMinutes(10), maxAttempts: 3, now);

        Assert.Equal(new VerificationStuckResetResult(ReturnedToPending: 0, Abandoned: 1), reset);
        await using (var verify = CreateDbContext())
        {
            var row = await verify.VerificationRequests.AsNoTracking().SingleAsync();
            Assert.Equal(VerificationRequestStatus.Error, row.Status);
            Assert.Equal(3, row.Attempts);
            Assert.Equal("Claim abandoned: no result recorded after 3 attempts", row.Error);
            Assert.Equal(now, row.CompletedUtc);
            Assert.Null(row.StartedUtc);
            Assert.Null(row.NextAttemptUtc);
        }

        await using var later = CreateDbContext();
        var laterRepository = Repository(later);
        Assert.Empty(await laterRepository.ClaimBatchAsync(10, now.AddDays(1)));
        Assert.Equal(0, await laterRepository.CountPendingAsync());
        Assert.Empty(await laterRepository.GetSourceNamesInUseAsync());
    }

    /// <summary>Starts from a claimed request, whose <c>StartedUtc</c> is set, so clearing it is
    /// what is exercised. A request fresh from <c>Create</c> has it null already.</summary>
    [Fact]
    public async Task RecordSourceError_OnAClaimedRequest_ClearsStartedUtcWhenSaved()
    {
        var claimedAt = T0.AddMinutes(1);
        await SeedEachAsync(Request("1"));

        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            var claimed = Assert.Single(await repository.ClaimBatchAsync(1, claimedAt));
            Assert.Equal(claimedAt, claimed.StartedUtc);

            claimed.RecordSourceError("HTTP 503", claimedAt.AddSeconds(30), maxAttempts: 3, backoff: TimeSpan.FromMinutes(1));
            await repository.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        var row = await verify.VerificationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(VerificationRequestStatus.Pending, row.Status);
        Assert.Null(row.StartedUtc);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(claimedAt.AddSeconds(30).AddMinutes(1), row.NextAttemptUtc);
    }

    // ── The open-request index and enqueueing ───────────────────────────────

    [Fact]
    public async Task OpenRequestIndex_RejectsASecondPendingRequestForTheSameEntity()
    {
        await SeedEachAsync(Request("42"));

        await using var db = CreateDbContext();
        db.VerificationRequests.Add(Request("42"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        AssertOpenRequestIndexViolation(ex);
    }

    /// <summary>The index's <c>Running</c> arm: a direct insert (no enqueue pre-check) of a Pending
    /// request while the entity's request is Running is refused by the index itself.</summary>
    [Fact]
    public async Task OpenRequestIndex_RejectsAPendingRequestWhileTheEntityHasARunningOne()
    {
        await SeedEachAsync(Request("42"));
        await using (var db = CreateDbContext())
            Assert.Equal(VerificationRequestStatus.Running, Assert.Single(await Repository(db).ClaimBatchAsync(1, T0.AddMinutes(1))).Status);

        await using var direct = CreateDbContext();
        direct.VerificationRequests.Add(Request("42"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => direct.SaveChangesAsync());

        AssertOpenRequestIndexViolation(ex);
    }

    [Fact]
    public async Task EnqueueAsync_KeysWithAPendingOrRunningRequest_AreSkipped_AndTheRestQueued()
    {
        const string running = "1", pending = "2", fresh = "3";
        var ids = await SeedEachAsync(
            Request(running, requestedUtc: T0.AddMinutes(-1)),
            Request(pending, requestedUtc: T0),
            // Same key, different entity type: not a conflict.
            Request(fresh, requestedUtc: T0, entityType: VerificationEntityType.Airline));
        await using (var db = CreateDbContext())
            Assert.Equal(ids[0], Assert.Single(await Repository(db).ClaimBatchAsync(1, T0)).Id);

        VerificationEnqueueResult result;
        await using (var db = CreateDbContext())
        {
            result = await Repository(db, now: T0.AddMinutes(5)).EnqueueAsync(
                VerificationEntityType.Airport, new[] { running, pending, fresh, fresh }, OurAirports,
                VerificationTrigger.Admin, "admin-oid", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 3), result);

        await using var verify = CreateDbContext();
        var airports = await verify.VerificationRequests.Where(r => r.EntityType == VerificationEntityType.Airport).ToListAsync();
        Assert.Equal(3, airports.Count);
        var queued = Assert.Single(airports, r => r.EntityKey == fresh);
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

    /// <summary>Keys are canonicalised before the open-request check, so <c>gb</c>, <c>GB</c> and
    /// <c>Gb</c> are one entity with one open request, stored as <c>GB</c>.</summary>
    [Fact]
    public async Task EnqueueAsync_CountryKeysDifferingOnlyInCase_AreOneOpenRequest()
    {
        VerificationEnqueueResult first, second;
        await using (var db = CreateDbContext())
            first = await Repository(db).EnqueueAsync(VerificationEntityType.Country, new[] { "gb" }, Mledoze, VerificationTrigger.Admin, "admin-oid", 0m);
        await using (var db = CreateDbContext())
            second = await Repository(db).EnqueueAsync(VerificationEntityType.Country, new[] { "GB", "Gb" }, Mledoze, VerificationTrigger.Admin, "admin-oid", 0m);

        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 0), first);
        Assert.Equal(new VerificationEnqueueResult(Queued: 0, Skipped: 2), second);

        await using var verify = CreateDbContext();
        Assert.Equal("GB", Assert.Single(await verify.VerificationRequests.AsNoTracking().ToListAsync()).EntityKey);
    }

    // ── Error cooldown (#418) ───────────────────────────────────────────────

    [Fact]
    public async Task EnqueueAsync_FlightTrigger_SkipsAKeyWhoseLastCheckErroredInsideTheCooldownWindow()
    {
        await SeedErroredKeyAsync("1", completedUtc: T0.AddHours(1));

        // now = T0+2h, cooldown 6h -> cutoff = T0-4h. The error completed at T0+1h: inside the window.
        VerificationEnqueueResult result;
        await using (var db = CreateDbContext())
        {
            result = await Repository(db, now: T0.AddHours(2), errorCooldown: Cooldown).EnqueueAsync(
                VerificationEntityType.Airport, new[] { "1" }, OurAirports, VerificationTrigger.Flight, "system", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 0, Skipped: 1), result);

        await using var verify = CreateDbContext();
        Assert.Single(await verify.VerificationRequests.Where(r => r.EntityKey == "1").ToListAsync());
    }

    [Fact]
    public async Task EnqueueAsync_FlightTrigger_QueuesAKeyOnceTheCooldownWindowHasPassed()
    {
        await SeedErroredKeyAsync("1", completedUtc: T0.AddHours(1));

        // now = T0+8h, cooldown 6h -> cutoff = T0+2h. The error completed at T0+1h: outside the window.
        VerificationEnqueueResult result;
        await using (var db = CreateDbContext())
        {
            result = await Repository(db, now: T0.AddHours(8), errorCooldown: Cooldown).EnqueueAsync(
                VerificationEntityType.Airport, new[] { "1" }, OurAirports, VerificationTrigger.Flight, "system", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 0), result);
    }

    /// <summary>
    /// R2: a cooldown skip must never reach an admin call, since <c>RetryVerificationRequestCommandHandler</c>
    /// turns any <c>Skipped &gt; 0</c> into a 409 claiming an open request exists -- a cooldown skip
    /// there would make that 409 lie. Proves both the result (queued despite the error) and the query
    /// count (no cooldown lookup added) by comparing against an otherwise-identical call for a key
    /// with no error history at all, rather than a hard-coded command count.
    /// </summary>
    [Fact]
    public async Task EnqueueAsync_AdminTrigger_QueuesDespiteAnErrorInsideTheWindow_AndIssuesNoExtraQuery()
    {
        const string coolingKey = "1", freshKey = "2";
        await SeedErroredKeyAsync(coolingKey, completedUtc: T0.AddHours(1));

        var coolingCommands = new CommandCounter();
        VerificationEnqueueResult coolingResult;
        await using (var db = CreateDbContext(coolingCommands))
        {
            coolingResult = await Repository(db, now: T0.AddHours(2), errorCooldown: Cooldown).EnqueueAsync(
                VerificationEntityType.Airport, new[] { coolingKey }, OurAirports, VerificationTrigger.Admin, "admin-oid", 0m);
        }

        var freshCommands = new CommandCounter();
        VerificationEnqueueResult freshResult;
        await using (var db = CreateDbContext(freshCommands))
        {
            freshResult = await Repository(db, now: T0.AddHours(2), errorCooldown: Cooldown).EnqueueAsync(
                VerificationEntityType.Airport, new[] { freshKey }, OurAirports, VerificationTrigger.Admin, "admin-oid", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 0), coolingResult);
        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 0), freshResult);
        Assert.Equal(freshCommands.Count, coolingCommands.Count);
    }

    [Fact]
    public async Task EnqueueAsync_FlightTrigger_LastCompletedWasVerified_IsNotSkipped()
    {
        await SeedEachAsync(Request("1", requestedUtc: T0));
        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Single(await repository.ClaimBatchAsync(1, T0)).MarkVerified(T0.AddHours(1));
            await repository.SaveChangesAsync();
        }

        VerificationEnqueueResult result;
        await using (var db = CreateDbContext())
        {
            result = await Repository(db, now: T0.AddHours(2), errorCooldown: Cooldown).EnqueueAsync(
                VerificationEntityType.Airport, new[] { "1" }, OurAirports, VerificationTrigger.Flight, "system", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 0), result);
    }

    [Fact]
    public async Task EnqueueAsync_FlightTrigger_LastCompletedWasMismatch_IsNotSkipped()
    {
        await SeedEachAsync(Request("1", requestedUtc: T0));
        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Single(await repository.ClaimBatchAsync(1, T0)).MarkMismatch(Differences, SourceValues, T0.AddHours(1));
            await repository.SaveChangesAsync();
        }

        VerificationEnqueueResult result;
        await using (var db = CreateDbContext())
        {
            result = await Repository(db, now: T0.AddHours(2), errorCooldown: Cooldown).EnqueueAsync(
                VerificationEntityType.Airport, new[] { "1" }, OurAirports, VerificationTrigger.Flight, "system", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 0), result);
    }

    [Fact]
    public async Task EnqueueAsync_FlightTrigger_LastCompletedWasNotFound_IsNotSkipped()
    {
        await SeedEachAsync(Request("1", requestedUtc: T0));
        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Single(await repository.ClaimBatchAsync(1, T0)).MarkNotFound(SourceValues, T0.AddHours(1));
            await repository.SaveChangesAsync();
        }

        VerificationEnqueueResult result;
        await using (var db = CreateDbContext())
        {
            result = await Repository(db, now: T0.AddHours(2), errorCooldown: Cooldown).EnqueueAsync(
                VerificationEntityType.Airport, new[] { "1" }, OurAirports, VerificationTrigger.Flight, "system", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 0), result);
    }

    /// <summary>
    /// An Error completed inside what would otherwise be the cooldown window, but a later, non-Error
    /// completion for the same key supersedes it (R3): the most recent outcome is what counts, not
    /// merely whether *an* Error happened to fall in the window. Deliberately gives the Verified
    /// request an old <c>RequestedUtc</c> and a recent <c>CompletedUtc</c> (it "took a while" -- e.g.
    /// retried), and the Error request the reverse (a recent <c>RequestedUtc</c>, resolved quickly):
    /// that inverts <c>RequestedUtc</c> order relative to <c>CompletedUtc</c> order between the two
    /// rows, which the open-request index permits (it only blocks two simultaneously-open rows for
    /// the same key, not this ordering of stored data), so a query that compared <c>RequestedUtc</c>
    /// instead of <c>CompletedUtc</c> would pick the Error as "most recent" and wrongly skip.
    /// </summary>
    [Fact]
    public async Task EnqueueAsync_FlightTrigger_OlderErrorSupersededByANewerNonErrorCompletion_IsNotSkipped()
    {
        const string key = "1";

        // Requested at T0, but not completed (Verified) until T0+19h.
        await SeedEachAsync(Request(key, requestedUtc: T0));
        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Single(await repository.ClaimBatchAsync(1, T0)).MarkVerified(T0.AddHours(19));
            await repository.SaveChangesAsync();
        }

        // A later check for the same key -- requested at T0+15h, after the Verified request's own
        // completion -- errors quickly, at T0+16h.
        await SeedEachAsync(Request(key, requestedUtc: T0.AddHours(15)));
        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Single(await repository.ClaimBatchAsync(1, T0.AddHours(15))).MarkError("boom", T0.AddHours(16));
            await repository.SaveChangesAsync();
        }

        // now = T0+20h, cooldown 6h -> cutoff = T0+14h. The Error's own CompletedUtc (T0+16h) is
        // inside that window -- in isolation it would look like a recent failure -- but the Verified
        // request's CompletedUtc (T0+19h) is later still, so it is the most recent outcome.
        VerificationEnqueueResult result;
        await using (var db = CreateDbContext())
        {
            result = await Repository(db, now: T0.AddHours(20), errorCooldown: Cooldown).EnqueueAsync(
                VerificationEntityType.Airport, new[] { key }, OurAirports, VerificationTrigger.Flight, "system", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 0), result);
    }

    /// <summary>R5: a cooldown skip counts in <c>Skipped</c> alongside an open-request skip, so the
    /// interface's <c>Queued + Skipped == keys.Count</c> invariant holds when both kinds occur together.</summary>
    [Fact]
    public async Task EnqueueAsync_FlightTrigger_OpenRequestSkipAndCooldownSkipTogether_QueuedPlusSkippedEqualsKeyCount()
    {
        const string openKey = "1", coolingKey = "2", freshKey = "3";
        await SeedEachAsync(Request(openKey, requestedUtc: T0)); // left Pending: open
        await SeedErroredKeyAsync(coolingKey, completedUtc: T0.AddHours(1));

        VerificationEnqueueResult result;
        await using (var db = CreateDbContext())
        {
            result = await Repository(db, now: T0.AddHours(2), errorCooldown: Cooldown).EnqueueAsync(
                VerificationEntityType.Airport, new[] { openKey, coolingKey, freshKey }, OurAirports,
                VerificationTrigger.Flight, "system", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 2), result);
        Assert.Equal(3, result.Queued + result.Skipped);
    }

    /// <summary>R4: the cooldown lookup is one batched query regardless of how many candidate keys it
    /// covers. Every key here is skipped, so no insert follows either -- exactly two commands total
    /// (the open-request check, then the cooldown lookup) however many keys are passed; a per-key
    /// implementation would grow with the key count instead.</summary>
    [Fact]
    public async Task EnqueueAsync_FlightTrigger_ManyKeysInTheCooldownWindow_CostsOneQuery()
    {
        var keys = new[] { "1", "2", "3", "4", "5" };
        foreach (var key in keys)
            await SeedErroredKeyAsync(key, completedUtc: T0.AddHours(1));

        var commands = new CommandCounter();
        VerificationEnqueueResult result;
        await using (var db = CreateDbContext(commands))
        {
            result = await Repository(db, now: T0.AddHours(2), errorCooldown: Cooldown).EnqueueAsync(
                VerificationEntityType.Airport, keys, OurAirports, VerificationTrigger.Flight, "system", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 0, Skipped: keys.Length), result);
        Assert.Equal(2, commands.Count);
    }

    /// <summary>R6: <see cref="TimeSpan.Zero"/> disables the cooldown and skips its query entirely --
    /// proven, like the admin test above, by comparing against a key with no error history rather than
    /// a hard-coded command count.</summary>
    [Fact]
    public async Task EnqueueAsync_FlightTrigger_ZeroCooldown_DisablesTheSkip_AndIssuesNoQuery()
    {
        const string coolingKey = "1", freshKey = "2";
        await SeedErroredKeyAsync(coolingKey, completedUtc: T0.AddHours(1));

        var zeroCooldownCommands = new CommandCounter();
        VerificationEnqueueResult zeroCooldownResult;
        await using (var db = CreateDbContext(zeroCooldownCommands))
        {
            zeroCooldownResult = await Repository(db, now: T0.AddHours(2), errorCooldown: TimeSpan.Zero).EnqueueAsync(
                VerificationEntityType.Airport, new[] { coolingKey }, OurAirports, VerificationTrigger.Flight, "system", 0m);
        }

        var baselineCommands = new CommandCounter();
        VerificationEnqueueResult baselineResult;
        await using (var db = CreateDbContext(baselineCommands))
        {
            baselineResult = await Repository(db, now: T0.AddHours(2), errorCooldown: TimeSpan.Zero).EnqueueAsync(
                VerificationEntityType.Airport, new[] { freshKey }, OurAirports, VerificationTrigger.Flight, "system", 0m);
        }

        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 0), zeroCooldownResult);
        Assert.Equal(new VerificationEnqueueResult(Queued: 1, Skipped: 0), baselineResult);
        Assert.Equal(baselineCommands.Count, zeroCooldownCommands.Count);
    }

    // ── Queries ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CountPendingAsync_CountsPendingOnly_DueOrNot()
    {
        var ids = await SeedEachAsync(
            Request("1", requestedUtc: T0.AddMinutes(-1)), // claimed below: Running
            Request("2", requestedUtc: T0),                // Pending
            Request("3", requestedUtc: T0),                // Pending, not due
            Request("4", requestedUtc: T0));               // Verified

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
    public async Task GetSourceNamesInUseAsync_ReturnsEveryWholeNameOnPendingAndRunningRequests_InOneQuery()
    {
        // Final fix wave, C3: one query replaces one per registered source.
        var ids = await SeedEachAsync(
            Request("1", sources: new[] { "ourairports", "aeroapi" }),
            Request("2", sources: new[] { "ourairports2" }),
            Request("3", sources: new[] { "aeroapi", "ourairports" }),
            Request("4", sources: new[] { "mledoze" }));
        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Equal(ids[0], Assert.Single(await repository.ClaimBatchAsync(1, T0)).Id); // Running
            (await repository.GetByIdAsync(ids[3]))!.MarkError("Entity no longer exists", T0); // completed
            await repository.SaveChangesAsync();
        }

        var commands = new CommandCounter();
        await using var verify = CreateDbContext(commands);
        var inUse = await Repository(verify).GetSourceNamesInUseAsync();

        Assert.Equal(new[] { "aeroapi", "ourairports", "ourairports2" }, inUse.Order(StringComparer.Ordinal));
        Assert.Equal(1, commands.Count);
    }

    /// <summary>
    /// Accept-source's guard (#378): only a Pending or Running request for that exact entity type and key
    /// counts as open.
    /// </summary>
    [Fact]
    public async Task HasOpenRequestAsync_IsTrueOnlyForAPendingOrRunningRequestOfThatEntityTypeAndKey()
    {
        var ids = await SeedEachAsync(
            Request("11"), // claimed below (the lowest id): Running
            Request("12"), // Pending
            Request("13"), // completed below
            Request("14", entityType: VerificationEntityType.Airline)); // Pending, another entity type
        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Equal(ids[0], Assert.Single(await repository.ClaimBatchAsync(1, T0)).Id);
            (await repository.GetByIdAsync(ids[2]))!.MarkError("Entity no longer exists", T0);
            await repository.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        var reader = Repository(verify);
        Assert.True(await reader.HasOpenRequestAsync(VerificationEntityType.Airport, "11")); // Running
        Assert.True(await reader.HasOpenRequestAsync(VerificationEntityType.Airport, "12")); // Pending
        Assert.False(await reader.HasOpenRequestAsync(VerificationEntityType.Airport, "13")); // completed
        Assert.False(await reader.HasOpenRequestAsync(VerificationEntityType.Airport, "14")); // open, but an Airline
        Assert.True(await reader.HasOpenRequestAsync(VerificationEntityType.Airline, "14"));
        Assert.False(await reader.HasOpenRequestAsync(VerificationEntityType.Airport, "15")); // never queued
    }

    /// <summary>
    /// Final fix wave, C4: the job returns its unfinished claims on shutdown. Of three claims this
    /// worker made, one was reset and claimed again by another worker, one had its outcome saved, and
    /// one is still this worker's: only that one goes back to Pending, with no attempt counted.
    /// </summary>
    [Fact]
    public async Task ReturnClaimsToPendingAsync_ReturnsOnlyClaimsStillRunningWithTheirOwnStamp_WithoutCountingAnAttempt()
    {
        var ids = await SeedEachAsync(
            Request("1", requestedUtc: T0),
            Request("2", requestedUtc: T0.AddMinutes(1)),
            Request("3", requestedUtc: T0.AddMinutes(2)));
        var takenOverAt = T0.AddMinutes(20);
        var claimedAt = T0.AddMinutes(21);

        // This worker claims 1; another worker resets that claim as stuck and claims 1 again.
        VerificationClaim takenOver;
        await using (var db = CreateDbContext())
            takenOver = VerificationClaim.From(Assert.Single(await Repository(db).ClaimBatchAsync(1, T0)));
        await using (var db = CreateDbContext())
        {
            var other = Repository(db, takenOverAt);
            Assert.Equal(1, (await other.ResetStuckRunningAsync(takenOverAt - TimeSpan.FromMinutes(10), maxAttempts: 3, takenOverAt)).ReturnedToPending);
            Assert.Equal(ids[0], Assert.Single(await other.ClaimBatchAsync(1, takenOverAt)).Id);
        }

        // This worker claims 2 and 3, and saves an outcome for 3.
        List<VerificationClaim> claims;
        await using (var db = CreateDbContext())
        {
            var repository = Repository(db, claimedAt);
            claims = (await repository.ClaimBatchAsync(5, claimedAt)).Select(VerificationClaim.From).ToList();
            Assert.Equal(new[] { ids[1], ids[2] }, claims.Select(c => c.RequestId));
            (await repository.GetByIdAsync(ids[2]))!.MarkVerified(claimedAt);
            await repository.SaveChangesAsync();
        }

        int returned;
        await using (var db = CreateDbContext())
            returned = await Repository(db).ReturnClaimsToPendingAsync(claims.Prepend(takenOver).ToList());

        Assert.Equal(1, returned);
        await using (var verify = CreateDbContext())
        {
            var rows = await verify.VerificationRequests.AsNoTracking().ToDictionaryAsync(r => r.Id);

            Assert.Equal(VerificationRequestStatus.Running, rows[ids[0]].Status); // the other worker's claim stands
            AssertUtc(takenOverAt, rows[ids[0]].StartedUtc);
            Assert.Equal(1, rows[ids[0]].Attempts);

            Assert.Equal(VerificationRequestStatus.Pending, rows[ids[1]].Status); // returned, no attempt
            Assert.Null(rows[ids[1]].StartedUtc);
            Assert.Equal(0, rows[ids[1]].Attempts);
            Assert.Null(rows[ids[1]].NextAttemptUtc);

            Assert.Equal(VerificationRequestStatus.Verified, rows[ids[2]].Status); // saved outcome stands
        }

        await using var next = CreateDbContext();
        Assert.Equal(ids[1], Assert.Single(await Repository(next).ClaimBatchAsync(5, claimedAt)).Id);
    }

    // ── Storage ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DifferencesAndSourceValues_RoundTripAsJsonb()
    {
        var ids = await SeedEachAsync(Request("9"));

        await using (var db = CreateDbContext())
        {
            var repository = Repository(db);
            Assert.Single(await repository.ClaimBatchAsync(1, T0.AddMinutes(1))).MarkMismatch(Differences, SourceValues, T0.AddMinutes(2));
            await repository.SaveChangesAsync();
        }

        await using var verify = CreateDbContext();
        var row = (await Repository(verify).GetByIdAsync(ids[0]))!;

        // jsonb normalises whitespace and key order, so compare as JSON, not as strings.
        Assert.Equal(VerificationRequestStatus.Mismatch, row.Status);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Differences), JsonNode.Parse(row.DifferencesJson!)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SourceValues), JsonNode.Parse(row.SourceValuesJson!)));

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
        var ids = await SeedEachAsync(Request("1", requestedUtc: T0), Request("2", requestedUtc: T0.AddSeconds(1)));

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
        var airportId = await SeedAirportAsync();
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

    private static VerificationRequestRepository Repository(FlightAssistantDbContext db, DateTime? now = null, TimeSpan? errorCooldown = null) =>
        new(db, new FakeTimeProvider(new DateTimeOffset(now ?? T0)), new FakeJobOptions(ErrorCooldown: errorCooldown ?? TimeSpan.Zero));

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

    /// <summary>Seeds one request for <paramref name="key"/>, requested at <c>T0</c>, claimed and
    /// completed as <see cref="VerificationRequestStatus.Error"/> at <paramref name="completedUtc"/>.</summary>
    private async Task SeedErroredKeyAsync(string key, DateTime completedUtc)
    {
        await SeedEachAsync(Request(key, requestedUtc: T0));
        await using var db = CreateDbContext();
        var repository = Repository(db);
        Assert.Single(await repository.ClaimBatchAsync(1, T0)).MarkError("boom", completedUtc);
        await repository.SaveChangesAsync();
    }

    private Task<long[]> SeedOrderingScenarioAsync() => SeedEachAsync(
        Request(AdminNew, VerificationTrigger.Admin, T0.AddMinutes(1)),
        Request(FlightNew, VerificationTrigger.Flight, T0.AddMinutes(2)),
        Request(AdminOld, VerificationTrigger.Admin, T0),
        Request(FlightOld, VerificationTrigger.Flight, T0.AddMinutes(1)));

    private async Task<int> SeedAirportAsync()
    {
        await using var seed = CreateDbContext();
        seed.Countries.Add(Country.Create("GB", "United Kingdom"));
        var airport = Airport.Create("LHR", "EGLL", "Heathrow", "London", "GB");
        seed.Airports.Add(airport);
        await seed.SaveChangesAsync();
        return airport.Id;
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

    private static void AssertOpenRequestIndexViolation(DbUpdateException ex)
    {
        var postgres = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal(VerificationRequestConfiguration.OpenRequestIndexName, postgres.ConstraintName);
    }

    private static void AssertUtc(DateTime expected, DateTime? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value);
        Assert.Equal(DateTimeKind.Utc, actual.Value.Kind);
    }

    /// <summary>Counts the queries a context sends.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
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
