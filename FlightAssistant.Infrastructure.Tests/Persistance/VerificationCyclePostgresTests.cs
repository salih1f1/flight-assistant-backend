using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using FlightAssistant.Application;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// Whole verification cycles (#367 Task 4) against real PostgreSQL: the Application runner and
/// processor from <c>AddApplication</c>, the real repositories on a real <c>DbContext</c> with the
/// real <see cref="AdminAuditInterceptor"/>, and the real <see cref="VerificationSourceRegistry"/>
/// over fake sources (no network). Everything the job depends on that InMemory cannot show is here:
/// the claim and reset SQL, the <c>xmin</c> token, <c>jsonb</c>, and the audit rows written in the
/// same transaction as each entity write.
///
/// <para>
/// <see cref="RunCycleAsync"/> stands in for <c>ReferenceVerificationBackgroundService</c>, but its
/// cycle scope deliberately carries <b>no</b> actor: every audit row asserted here can only come
/// from the scopes the runner creates for each request.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class VerificationCyclePostgresTests : PostgresTestBase
{
    private static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresContainerFixture _fixture;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(T0));

    public VerificationCyclePostgresTests(PostgresContainerFixture fixture) : base(fixture)
    {
        _fixture = fixture;
    }

    private static Country UnitedKingdom() => Country.Create("GB", "United Kingdom", "GBR", 826, "Europe", "Northern Europe");
    private static Country France(string name = "France") => Country.Create("FR", name, "FRA", 250, "Europe", "Western Europe");
    private static Airport Heathrow() => Airport.Create("LHR", "EGLL", "London Heathrow", "London", "GB", 51.4706, -0.461941, 83);
    private static Airport CharlesDeGaulle() => Airport.Create("CDG", "LFPG", "Paris Charles de Gaulle", "Paris", "FR", 49.0097, 2.5479, 392);

    // ── Outcomes and auditing ───────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_VerifiesMatches_FlagsMismatchesAndMissingRows_AndAuditsEachEntityWriteAsASystemVerify()
    {
        await SeedAsync(UnitedKingdom(), France());
        var lhr = Heathrow();
        var cdg = CharlesDeGaulle();
        await SeedAsync(lhr, cdg);

        var lhrRequest = await EnqueueAsync(VerificationEntityType.Airport, Key(lhr.Id), "ourairports");
        var cdgRequest = await EnqueueAsync(VerificationEntityType.Airport, Key(cdg.Id), "ourairports");
        var gbRequest = await EnqueueAsync(VerificationEntityType.Country, "GB", "mledoze");
        var frRequest = await EnqueueAsync(VerificationEntityType.Country, "FR", "mledoze");
        var deletedRequest = await EnqueueAsync(VerificationEntityType.Airport, "999999", "ourairports");
        var aeroApiRequest = await EnqueueAsync(VerificationEntityType.Airline, "1", "aeroapi");

        var ourAirports = new FakeSource("ourairports", VerificationEntityType.Airport, key =>
            key.Iata == "LHR" ? Found(VerificationCheckedColumns.Project(Heathrow())) : VerificationSourceFetchResult.NotFound);
        var mledoze = new FakeSource("mledoze", VerificationEntityType.Country, key => key.CountryCode switch
        {
            "GB" => Found(VerificationCheckedColumns.Project(UnitedKingdom())),
            "FR" => Found(VerificationCheckedColumns.Project(France(name: "French Republic"))),
            _ => VerificationSourceFetchResult.NotFound
        });
        await using var job = BuildJob(new IVerificationSource[] { ourAirports, mledoze });

        var result = await RunCycleAsync(job);

        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 6, RequestsFailed: 2, ClaimsLost: 0, FailuresUnrecorded: 0), result);

        await using var verify = CreateDbContext();
        var airports = await verify.Airports.AsNoTracking().ToDictionaryAsync(a => a.Iata);
        AssertVerifiedBySystem(airports["LHR"], "ourairports");
        AssertNeedsAttention(airports["CDG"], "Not found in ourairports");
        var countries = await verify.Countries.AsNoTracking().ToDictionaryAsync(c => c.Code);
        AssertVerifiedBySystem(countries["GB"], "mledoze");
        AssertNeedsAttention(countries["FR"], "Name differ (mledoze)");

        var requests = await verify.VerificationRequests.AsNoTracking().ToDictionaryAsync(r => r.Id);
        Assert.Equal(VerificationRequestStatus.Verified, requests[lhrRequest].Status);
        Assert.Equal(VerificationRequestStatus.Verified, requests[gbRequest].Status);

        Assert.Equal(VerificationRequestStatus.NotFound, requests[cdgRequest].Status);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{}"), JsonNode.Parse(requests[cdgRequest].SourceValuesJson!)));

        var frRow = requests[frRequest];
        Assert.Equal(VerificationRequestStatus.Mismatch, frRow.Status);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""[{"column":"Name","ours":"France","source":"French Republic","sourceName":"mledoze"}]"""),
            JsonNode.Parse(frRow.DifferencesJson!)));
        Assert.Equal("French Republic", JsonNode.Parse(frRow.SourceValuesJson!)!["mledoze"]!["Name"]!.GetValue<string>());

        Assert.Equal(VerificationRequestStatus.Error, requests[deletedRequest].Status);
        Assert.Equal("Entity no longer exists", requests[deletedRequest].Error);
        Assert.Equal(VerificationRequestStatus.Error, requests[aeroApiRequest].Status);
        Assert.Equal("Source 'aeroapi' is not registered.", requests[aeroApiRequest].Error);

        Assert.All(requests.Values, r =>
        {
            Assert.Equal(T0, r.CompletedUtc);
            Assert.Equal(0, r.Attempts);
        });

        // One audit row per entity write -- none for the six request status changes -- all a system Verify.
        var audit = await verify.AdminAuditLogs.AsNoTracking().ToListAsync();
        Assert.Equal(
            new[] { ("Airport", Key(cdg.Id)), ("Airport", Key(lhr.Id)), ("Country", "FR"), ("Country", "GB") }.Order(),
            audit.Select(a => (a.EntityType, a.EntityId!)).Order());
        Assert.All(audit, a =>
        {
            Assert.Equal(AdminAuditAction.Verify, a.Action);
            Assert.Equal("system", a.ActorId);
            Assert.Equal("Verification job", a.ActorName);
        });
        Assert.DoesNotContain(audit, a => a.EntityType == nameof(VerificationRequest));
        Assert.NotNull(Assert.Single(audit.Select(a => a.TraceId).Distinct()));

        // Nothing is open any more, so both sources' datasets are released.
        Assert.Equal(1, ourAirports.ReleaseCount);
        Assert.Equal(1, mledoze.ReleaseCount);
    }

    /// <summary>
    /// The processor recognises its claim by the <c>StartedUtc</c> read back from PostgreSQL, whose
    /// <c>timestamptz</c> keeps microseconds. Every other test here uses whole seconds; a real clock
    /// reading has 100-ns ticks, and a claim built from that in-memory reading would never equal the
    /// stored value, so every production request would be skipped as a lost claim.
    /// </summary>
    [Fact]
    public async Task RunCycle_AtAClockReadingFinerThanAMicrosecond_ProcessesItsClaim_NotClaimLost()
    {
        var now = T0.AddTicks(1_234_567); // .1234567 s: the last tick digit is below timestamptz's resolution
        _time.SetUtcNow(new DateTimeOffset(now));
        await SeedAsync(UnitedKingdom());
        await EnqueueAsync(VerificationEntityType.Country, "GB", "mledoze");
        var mledoze = new FakeSource("mledoze", VerificationEntityType.Country, _ => Found(VerificationCheckedColumns.Project(UnitedKingdom())));
        await using var job = BuildJob(new IVerificationSource[] { mledoze });

        var result = await RunCycleAsync(job);

        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 1, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0), result);

        await using var verify = CreateDbContext();
        var row = await verify.VerificationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(VerificationRequestStatus.Verified, row.Status);
        // The premise: the stored claim time is not the in-memory clock reading.
        Assert.NotEqual((DateTime?)now, row.StartedUtc);
        Assert.True((await verify.Countries.AsNoTracking().SingleAsync()).IsVerified);
    }

    // ── Stuck requests ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_ResetsAClaimStuckForOverTenMinutes_AndProcessesIt_LeavingAYoungerClaimAlone()
    {
        await SeedAsync(UnitedKingdom(), France());
        var stuck = await EnqueueAsync(VerificationEntityType.Country, "GB", "mledoze", requestedUtc: T0.AddHours(-2));
        var young = await EnqueueAsync(VerificationEntityType.Country, "FR", "mledoze", requestedUtc: T0.AddHours(-1));

        // Another worker claimed GB 11 minutes ago and died; it claimed FR 9 minutes ago and is still working.
        Assert.Equal(stuck, Assert.Single(await ClaimOnAnotherWorkerAsync(T0.AddMinutes(-11), batchSize: 1)).Id);
        Assert.Equal(young, Assert.Single(await ClaimOnAnotherWorkerAsync(T0.AddMinutes(-9), batchSize: 1)).Id);

        var mledoze = new FakeSource("mledoze", VerificationEntityType.Country, key =>
            Found(VerificationCheckedColumns.Project(key.CountryCode == "GB" ? UnitedKingdom() : France())));
        await using var job = BuildJob(new IVerificationSource[] { mledoze });

        var result = await RunCycleAsync(job);

        Assert.Equal(new VerificationCycleResult(StuckReset: 1, StuckAbandoned: 0, RequestsProcessed: 1, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0), result);
        Assert.Equal(VerificationSourceLookupKey.ForCountry("GB"), Assert.Single(mledoze.Lookups));

        await using var verify = CreateDbContext();
        var rows = await verify.VerificationRequests.AsNoTracking().ToDictionaryAsync(r => r.Id);
        Assert.Equal(VerificationRequestStatus.Verified, rows[stuck].Status);
        Assert.Equal(T0, rows[stuck].StartedUtc);
        Assert.Equal(1, rows[stuck].Attempts); // the dead worker's abandoned claim, counted by the reset

        Assert.Equal(VerificationRequestStatus.Running, rows[young].Status);
        Assert.Equal(T0.AddMinutes(-9), rows[young].StartedUtc);
        Assert.Null(rows[young].CompletedUtc);

        var countries = await verify.Countries.AsNoTracking().ToDictionaryAsync(c => c.Code);
        AssertVerifiedBySystem(countries["GB"], "mledoze");
        Assert.False(countries["FR"].IsVerified);
    }

    // ── Source errors and backoff ───────────────────────────────────────────

    [Fact]
    public async Task RunCycle_SourceErrors_BackOffOneThenFiveMinutes_AreNotReclaimedEarly_AndEndInErrorWithTheEntityUnchanged()
    {
        var gb = UnitedKingdom();
        var manuallyVerifiedAt = T0.AddDays(-1);
        gb.Verify("admin-oid", VerificationSources.Manual, manuallyVerifiedAt);
        await SeedAsync(gb);
        var requestId = await EnqueueAsync(VerificationEntityType.Country, "GB", "mledoze");

        var mledoze = new FakeSource("mledoze", VerificationEntityType.Country, _ => VerificationSourceFetchResult.SourceError("HTTP 503"));
        await using var job = BuildJob(new IVerificationSource[] { mledoze }, maxAttempts: 3);

        // Attempt 1 fails: retry after one minute. A request waiting to retry still uses its source.
        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 1, RequestsFailed: 1, ClaimsLost: 0, FailuresUnrecorded: 0), await RunCycleAsync(job));
        await AssertPendingAsync(requestId, attempts: 1, nextAttemptUtc: T0.AddMinutes(1));
        Assert.Equal(0, mledoze.ReleaseCount);

        // Not due yet: not claimed and not looked up again.
        _time.SetUtcNow(T0.AddSeconds(59));
        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 0, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0), await RunCycleAsync(job));
        Assert.Single(mledoze.Lookups);
        await AssertPendingAsync(requestId, attempts: 1, nextAttemptUtc: T0.AddMinutes(1));

        // Attempt 2 once due: retry after five minutes.
        _time.SetUtcNow(T0.AddMinutes(1));
        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 1, RequestsFailed: 1, ClaimsLost: 0, FailuresUnrecorded: 0), await RunCycleAsync(job));
        await AssertPendingAsync(requestId, attempts: 2, nextAttemptUtc: T0.AddMinutes(6));

        _time.SetUtcNow(T0.AddMinutes(5));
        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 0, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0), await RunCycleAsync(job));
        Assert.Equal(2, mledoze.Lookups.Count);

        // Attempt 3 reaches MaxAttempts: Error, and nothing open uses the source any more.
        _time.SetUtcNow(T0.AddMinutes(6));
        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 1, RequestsFailed: 1, ClaimsLost: 0, FailuresUnrecorded: 0), await RunCycleAsync(job));
        Assert.Equal(3, mledoze.Lookups.Count);
        Assert.Equal(1, mledoze.ReleaseCount);

        await using var verify = CreateDbContext();
        var row = await verify.VerificationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(VerificationRequestStatus.Error, row.Status);
        Assert.Equal(3, row.Attempts);
        Assert.Equal("mledoze: HTTP 503", row.Error);
        Assert.Equal(T0.AddMinutes(6), row.CompletedUtc);
        Assert.Null(row.NextAttemptUtc);

        var country = await verify.Countries.AsNoTracking().SingleAsync();
        Assert.True(country.IsVerified);
        Assert.Equal("admin-oid", country.VerifiedBy);
        Assert.Equal(VerificationSources.Manual, country.VerificationSource);
        Assert.Equal(manuallyVerifiedAt, country.VerifiedAt);
        Assert.Empty(await verify.AdminAuditLogs.ToListAsync());
    }

    /// <summary>
    /// A request whose claims keep ending with no recorded result (a crash, or a failure that could
    /// not itself be recorded) used to come back every 10 minutes forever. On its last attempt the
    /// cycle's reset completes it as Error: it is not claimed or looked up, and its source is released.
    /// </summary>
    [Fact]
    public async Task RunCycle_AStuckRequestOnItsLastAttempt_IsAbandonedAsError_NotReclaimed_AndItsSourceReleased()
    {
        await SeedAsync(UnitedKingdom());
        await EnqueueAsync(VerificationEntityType.Country, "GB", "mledoze", requestedUtc: T0.AddHours(-2));

        // Two earlier claims ended in recorded source errors; a third, 11 minutes ago, recorded nothing.
        for (var i = 0; i < 2; i++)
        {
            var at = T0.AddMinutes(-40 + i);
            await using var db = CreateDbContext();
            var worker = AnotherWorker(db, at);
            Assert.Single(await worker.ClaimBatchAsync(1, at))
                .RecordSourceError("HTTP 503", at, maxAttempts: 3, backoff: TimeSpan.Zero);
            await worker.SaveChangesAsync();
        }
        Assert.Single(await ClaimOnAnotherWorkerAsync(T0.AddMinutes(-11), batchSize: 1));

        // Holding the dataset an earlier cycle loaded, so there is something to release.
        var mledoze = new FakeSource("mledoze", VerificationEntityType.Country, _ => Found(VerificationCheckedColumns.Project(UnitedKingdom())))
        {
            HasCachedData = true
        };
        await using var job = BuildJob(new IVerificationSource[] { mledoze }, maxAttempts: 3);

        var result = await RunCycleAsync(job);

        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 1, RequestsProcessed: 0, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0), result);
        Assert.Empty(mledoze.Lookups);
        Assert.Equal(1, mledoze.ReleaseCount);

        await using var verify = CreateDbContext();
        var row = await verify.VerificationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(VerificationRequestStatus.Error, row.Status);
        Assert.Equal(3, row.Attempts);
        Assert.Equal(VerificationRequest.ClaimAbandonedError(3), row.Error);
        Assert.Equal(T0, row.CompletedUtc);
        Assert.Null(row.StartedUtc);
        Assert.False((await verify.Countries.AsNoTracking().SingleAsync()).IsVerified);
        Assert.Empty(await verify.AdminAuditLogs.ToListAsync());
    }

    /// <summary>
    /// Final fix wave, A: a failed dataset load is kept for the failure cooldown, so a batch of
    /// requests against a failing upstream downloads once, not once per request. Uses the real
    /// <see cref="OurAirportsVerificationSource"/> over a counting 503 handler.
    /// </summary>
    [Fact]
    public async Task RunCycle_ABatchAgainstAFailingSource_DownloadsItsDatasetOnce_AndEveryRequestGetsTheSourceError()
    {
        await SeedAsync(UnitedKingdom(), France());
        var airports = new[]
        {
            Heathrow(),
            CharlesDeGaulle(),
            Airport.Create("LGW", "EGKK", "London Gatwick", "London", "GB", 51.1481, -0.1903, 202)
        };
        await SeedAsync(airports);
        foreach (var airport in airports)
            await EnqueueAsync(VerificationEntityType.Airport, Key(airport.Id), OurAirportsVerificationSource.SourceName);

        var handler = new CountingHandler(HttpStatusCode.ServiceUnavailable);
        var ourAirports = new OurAirportsVerificationSource(
            new SingleClientFactory(handler), _time, NullLogger<OurAirportsVerificationSource>.Instance, TimeSpan.FromSeconds(5));
        await using var job = BuildJob(new IVerificationSource[] { ourAirports });

        var result = await RunCycleAsync(job);

        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 3, RequestsFailed: 3, ClaimsLost: 0, FailuresUnrecorded: 0), result);
        Assert.Equal(1, handler.CallCount);

        await using var verify = CreateDbContext();
        var rows = await verify.VerificationRequests.AsNoTracking().ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(VerificationRequestStatus.Pending, r.Status);
            Assert.Equal(1, r.Attempts);
            Assert.StartsWith("ourairports: ", r.Error);
        });
        Assert.Single(rows.Select(r => r.Error).Distinct());
    }

    // ── Shutdown mid-batch ──────────────────────────────────────────────────

    /// <summary>
    /// Final fix wave, C4: the host stops while the second of three claimed requests is being looked
    /// up. The first keeps its saved outcome; the second and third go back to Pending with no attempt
    /// counted and no claim stamp, so the next cycle claims them at once instead of the stuck-request
    /// reset spending an attempt on each after 10 minutes.
    /// </summary>
    [Fact]
    public async Task RunCycle_CancelledMidBatch_ReturnsTheUnfinishedClaimsToPending_WithoutAnAttempt_AndKeepsTheFinishedOutcome()
    {
        await SeedAsync(UnitedKingdom(), France(), Country.Create("DE", "Germany", "DEU", 276, "Europe", "Western Europe"));
        var gb = await EnqueueAsync(VerificationEntityType.Country, "GB", "mledoze", requestedUtc: T0.AddHours(-3));
        var fr = await EnqueueAsync(VerificationEntityType.Country, "FR", "mledoze", requestedUtc: T0.AddHours(-2));
        var de = await EnqueueAsync(VerificationEntityType.Country, "DE", "mledoze", requestedUtc: T0.AddHours(-1));

        using var shutdown = new CancellationTokenSource();
        var mledoze = new FakeSource("mledoze", VerificationEntityType.Country, key =>
        {
            if (key.CountryCode == "FR")
            {
                shutdown.Cancel();
                shutdown.Token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(Found(VerificationCheckedColumns.Project(UnitedKingdom())));
        });
        await using var job = BuildJob(new IVerificationSource[] { mledoze });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunCycleAsync(job, shutdown.Token));

        await using var verify = CreateDbContext();
        var rows = await verify.VerificationRequests.AsNoTracking().ToDictionaryAsync(r => r.Id);
        Assert.Equal(VerificationRequestStatus.Verified, rows[gb].Status);
        foreach (var id in new[] { fr, de })
        {
            Assert.Equal(VerificationRequestStatus.Pending, rows[id].Status);
            Assert.Null(rows[id].StartedUtc);
            Assert.Equal(0, rows[id].Attempts);
            Assert.Null(rows[id].Error);
            Assert.Null(rows[id].CompletedUtc);
        }

        Assert.Equal(new[] { fr, de }, (await ClaimOnAnotherWorkerAsync(T0, batchSize: 10)).Select(r => r.Id));
    }

    // ── Lost claims ─────────────────────────────────────────────────────────

    /// <summary>
    /// Mid-lookup, another worker resets this cycle's claim as stuck and claims the request again.
    /// The verified outcome's save then fails on the <c>xmin</c> token: nothing is written -- no
    /// entity <c>Verify</c>, no audit row, no attempt of this cycle's own (the other worker's reset
    /// counts the abandoned claim) -- and the other worker's claim stands.
    /// </summary>
    [Fact]
    public async Task RunCycle_ClaimLostBeforeTheSave_DiscardsTheOutcome_WithoutAnAttempt_AndTheNewClaimStands()
    {
        await SeedAsync(UnitedKingdom());
        await EnqueueAsync(VerificationEntityType.Country, "GB", "mledoze");
        var reclaimedAt = T0.AddMinutes(20);

        var mledoze = new FakeSource("mledoze", VerificationEntityType.Country, async _ =>
        {
            Assert.Equal(1, (await ResetOnAnotherWorkerAsync(reclaimedAt)).ReturnedToPending);
            Assert.Single(await ClaimOnAnotherWorkerAsync(reclaimedAt, batchSize: 1));
            return Found(VerificationCheckedColumns.Project(UnitedKingdom()));
        });
        await using var job = BuildJob(new IVerificationSource[] { mledoze });

        var result = await RunCycleAsync(job);

        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 1, RequestsFailed: 0, ClaimsLost: 1, FailuresUnrecorded: 0), result);

        await using var verify = CreateDbContext();
        var row = await verify.VerificationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(VerificationRequestStatus.Running, row.Status);
        Assert.Equal(reclaimedAt, row.StartedUtc);
        // One attempt, counted by the other worker's reset of this cycle's claim; this cycle recorded none.
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.Error);
        Assert.Null(row.CompletedUtc);

        Assert.False((await verify.Countries.AsNoTracking().SingleAsync()).IsVerified);
        Assert.Empty(await verify.AdminAuditLogs.ToListAsync());
    }

    /// <summary>
    /// While the batch's first request is being looked up, another worker resets both claims as
    /// stuck and claims both again. The second request, re-loaded afterwards, carries the other
    /// worker's claim and a fresh concurrency token; it must be skipped, not processed and saved
    /// over that claim.
    /// </summary>
    [Fact]
    public async Task RunCycle_AClaimTakenOverBeforeItsTurn_IsSkipped_AndTheOtherWorkersClaimIsNotOverwritten()
    {
        await SeedAsync(UnitedKingdom(), France());
        var first = await EnqueueAsync(VerificationEntityType.Country, "GB", "mledoze", requestedUtc: T0.AddHours(-2));
        var second = await EnqueueAsync(VerificationEntityType.Country, "FR", "mledoze", requestedUtc: T0.AddHours(-1));
        var reclaimedAt = T0.AddMinutes(20);

        var mledoze = new FakeSource("mledoze", VerificationEntityType.Country, async key =>
        {
            if (key.CountryCode == "GB")
            {
                Assert.Equal(2, (await ResetOnAnotherWorkerAsync(reclaimedAt)).ReturnedToPending);
                Assert.Equal(2, (await ClaimOnAnotherWorkerAsync(reclaimedAt, batchSize: 2)).Count);
            }
            return Found(VerificationCheckedColumns.Project(key.CountryCode == "GB" ? UnitedKingdom() : France()));
        });
        await using var job = BuildJob(new IVerificationSource[] { mledoze });

        var result = await RunCycleAsync(job);

        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 2, RequestsFailed: 0, ClaimsLost: 2, FailuresUnrecorded: 0), result);
        Assert.Equal(VerificationSourceLookupKey.ForCountry("GB"), Assert.Single(mledoze.Lookups));

        await using var verify = CreateDbContext();
        var rows = await verify.VerificationRequests.AsNoTracking().ToDictionaryAsync(r => r.Id);
        foreach (var id in new[] { first, second })
        {
            Assert.Equal(VerificationRequestStatus.Running, rows[id].Status);
            Assert.Equal(reclaimedAt, rows[id].StartedUtc);
            Assert.Equal(1, rows[id].Attempts); // counted by the other worker's reset; this cycle recorded none
        }

        Assert.DoesNotContain(await verify.Countries.AsNoTracking().ToListAsync(), c => c.IsVerified);
        Assert.Empty(await verify.AdminAuditLogs.ToListAsync());
    }

    // ── The replacement claim excludes by source name, not just paused ids (#428) ───────────

    /// <summary>
    /// #428: proved against real PostgreSQL that #401's original G1 -- excluding only the ids just
    /// paused -- isn't enough once the backlog of open aeroapi requests is at least two batches deep.
    /// Four flight-triggered aeroapi requests sit ahead of two ourairports requests for real seeded
    /// airports; BatchSize is 2, so P (4) &gt;= 2 x BatchSize. The initial claim pauses its two aeroapi
    /// requests; the fixed replacement claim excludes the aeroapi source entirely, reaching straight
    /// past the other two, not-yet-tried aeroapi requests to the free-source ones behind them, which
    /// are actually verified -- not merely "not excluded by id". A lookups-only assertion would not
    /// tell the two apart: with no airport rows seeded, the free requests would end as Error under
    /// both the broken and the fixed code, so this test seeds real airports and asserts each one's
    /// <c>IsVerified</c> directly.
    /// </summary>
    [Fact]
    public async Task RunCycle_G1_DeepFlightTriggeredAeroApiBacklog_TheReplacementClaimStillReachesFreeSourceWork() =>
        await AssertReplacementClaimReachesFreeSourceWorkAsync(VerificationTrigger.Flight);

    /// <summary>Same scenario (#428), admin-triggered: the fix does not depend on the flight trigger's
    /// own priority in the claim's <c>ORDER BY</c> -- source-name exclusion works the same when every
    /// request, gated and free alike, is ordered by <c>requested_utc</c> alone.</summary>
    [Fact]
    public async Task RunCycle_G1_DeepAdminTriggeredAeroApiBacklog_TheReplacementClaimStillReachesFreeSourceWork() =>
        await AssertReplacementClaimReachesFreeSourceWorkAsync(VerificationTrigger.Admin);

    private async Task AssertReplacementClaimReachesFreeSourceWorkAsync(VerificationTrigger backlogTrigger)
    {
        await SeedAsync(UnitedKingdom(), France());
        var lhr = Heathrow();
        var cdg = CharlesDeGaulle();
        await SeedAsync(lhr, cdg);

        // Admin-triggered: four aeroapi requests old enough to be promoted (#419) rank ahead of the
        // two free-source requests below by requested_utc alone -- the scenario this variant is named
        // for, since #428's fix does not depend on the flight trigger's own priority in the claim's
        // ORDER BY. Flight-triggered: every request below is kept inside the default 10-minute
        // ClaimPromotionAge, so none of them is promoted -- what ranks the four aeroapi requests ahead
        // here is the top-tier predicate's *trigger* branch (trigger = flight OR requested_utc <=
        // promotedAtOrBeforeUtc) alone. The free-source requests are deliberately the chronologically
        // *older* pair (still inside the window, so still unpromoted): age alone would put them first,
        // so aeroapi landing ahead of them here can only be the trigger branch, not a coincidence of
        // timestamps. If these requests were old enough to be promoted too, as the admin variant's
        // are, both branches would hold for all six requests and this variant would no longer be
        // distinguishable from the admin one (#419 broke this test's original timestamps the same way).
        var isFlightVariant = backlogTrigger == VerificationTrigger.Flight;

        for (var i = 1; i <= 4; i++)
            await EnqueueAsync(
                VerificationEntityType.Airline, i.ToString(CultureInfo.InvariantCulture), "aeroapi",
                requestedUtc: isFlightVariant ? T0.AddMinutes(-5 + i) : T0.AddHours(-10 + i),
                trigger: backlogTrigger);

        var lhrRequest = await EnqueueAsync(
            VerificationEntityType.Airport, Key(lhr.Id), "ourairports",
            requestedUtc: isFlightVariant ? T0.AddMinutes(-8) : T0.AddHours(-1));
        var cdgRequest = await EnqueueAsync(
            VerificationEntityType.Airport, Key(cdg.Id), "ourairports",
            requestedUtc: isFlightVariant ? T0.AddMinutes(-7) : T0.AddMinutes(-30));

        // The aeroapi lookup is never reached (paused before fetch); NotFound is just a harmless stub.
        var aeroApi = new FakeSource("aeroapi", VerificationEntityType.Airline, _ => VerificationSourceFetchResult.NotFound);
        var ourAirports = new FakeSource("ourairports", VerificationEntityType.Airport, key =>
            key.Iata == "LHR" ? Found(VerificationCheckedColumns.Project(Heathrow())) : Found(VerificationCheckedColumns.Project(CharlesDeGaulle())));
        await using var job = BuildJob(
            new IVerificationSource[] { aeroApi, ourAirports }, batchSize: 2, budgetState: ProviderBudgetState.Exhausted);

        var result = await RunCycleAsync(job);

        // P (4) >= 2 x BatchSize (2): the initial claim pauses 2, and the replacement claim -- fixed
        // to exclude the aeroapi source, not just those 2 ids -- reaches the 2 free requests instead
        // of pausing 2 more of the remaining, not-yet-tried aeroapi requests (the broken behaviour).
        // Restoring the paused-id exclusion (#401's original G1) makes this assertion fail with
        // Paused: 4, not 2 -- the regression this test actually guards against.
        Assert.Equal(
            new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 4, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0, Paused: 2),
            result);
        Assert.Empty(aeroApi.Lookups);

        await using var verify = CreateDbContext();
        var requests = await verify.VerificationRequests.AsNoTracking().ToDictionaryAsync(r => r.Id);
        Assert.Equal(VerificationRequestStatus.Verified, requests[lhrRequest].Status);
        Assert.Equal(VerificationRequestStatus.Verified, requests[cdgRequest].Status);
        Assert.True((await verify.Airports.AsNoTracking().SingleAsync(a => a.Id == lhr.Id)).IsVerified);
        Assert.True((await verify.Airports.AsNoTracking().SingleAsync(a => a.Id == cdg.Id)).IsVerified);

        // Every aeroapi request -- the 2 paused, and the 2 the replacement claim never even touched
        // -- is left exactly as it was: still Pending, no attempt, no error.
        var aeroApiRequests = requests.Values.Where(r => r.EntityType == VerificationEntityType.Airline).ToList();
        Assert.Equal(4, aeroApiRequests.Count);
        Assert.All(aeroApiRequests, r =>
        {
            Assert.Equal(VerificationRequestStatus.Pending, r.Status);
            Assert.Equal(0, r.Attempts);
            Assert.Null(r.Error);
        });
    }

    // ── #419 × #401/#428: promotion does not reopen the starvation #428 closed ───

    /// <summary>
    /// A deep, promoted, paused admin <c>aeroapi</c> backlog does not starve the younger
    /// flight-triggered free-source work behind it, now that the replacement claim excludes by
    /// source name (#428) rather than by the ids the initial batch just paused (#401's original G1).
    /// Seven admin-triggered <c>aeroapi</c> requests, each an hour old -- promoted, since the default
    /// <c>ClaimPromotionAge</c> is 10 minutes -- rank ahead of two younger flight-triggered
    /// <c>ourairports</c> requests for real seeded airports. <c>BatchSize</c> is 3, so P (7) &gt;= 2 x
    /// BatchSize (6): with the AeroAPI budget Exhausted from the first read,
    /// the initial claim pauses three of the promoted <c>aeroapi</c> requests, and #428's replacement
    /// claim -- excluding the whole <c>aeroapi</c> source rather than just those three ids -- reaches
    /// past the four still-untried <c>aeroapi</c> requests straight to the two free-source ones,
    /// which are both actually verified, not merely "not excluded by id". The other four
    /// <c>aeroapi</c> requests are never claimed at all this cycle.
    ///
    /// <para>
    /// The break-proof reverts the replacement claim to id-only exclusion (#401's original G1) and
    /// captures that this test then fails: with only the three just-paused ids excluded, the
    /// replacement batch fills on the next three oldest pending requests -- three more promoted
    /// <c>aeroapi</c> requests -- pausing them too and leaving both free-source requests unreached
    /// for the whole cycle, the exact shape #428 fixed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunCycle_ManyPromotedPausedAeroApiRequests_TheReplacementClaimStillReachesYoungerFlightTriggeredFreeSourceWork()
    {
        await SeedAsync(UnitedKingdom(), France());
        var lhr = Heathrow();
        var cdg = CharlesDeGaulle();
        await SeedAsync(lhr, cdg);

        var oldAdminAeroApiIds = new List<long>();
        for (var i = 0; i < 7; i++)
            oldAdminAeroApiIds.Add(await EnqueueAsync(
                VerificationEntityType.Airline, (300 + i).ToString(CultureInfo.InvariantCulture), "aeroapi",
                requestedUtc: T0.AddHours(-1).AddSeconds(i))); // all promoted: an hour old vs. the default 10-minute ClaimPromotionAge

        var lhrRequest = await EnqueueAsync(VerificationEntityType.Airport, Key(lhr.Id), "ourairports", requestedUtc: T0, trigger: VerificationTrigger.Flight);
        var cdgRequest = await EnqueueAsync(VerificationEntityType.Airport, Key(cdg.Id), "ourairports", requestedUtc: T0.AddSeconds(1), trigger: VerificationTrigger.Flight);

        // The aeroapi lookup is never reached (paused, or not claimed at all, before fetch); NotFound
        // is just a harmless stub.
        var aeroApi = new FakeSource("aeroapi", VerificationEntityType.Airline, _ => VerificationSourceFetchResult.NotFound);
        var ourAirports = new FakeSource("ourairports", VerificationEntityType.Airport, key =>
            key.Iata == "LHR" ? Found(VerificationCheckedColumns.Project(Heathrow())) : Found(VerificationCheckedColumns.Project(CharlesDeGaulle())));
        await using var job = BuildJob(
            new IVerificationSource[] { aeroApi, ourAirports }, batchSize: 3, budgetState: ProviderBudgetState.Exhausted);

        var result = await RunCycleAsync(job);

        // The initial batch (3) pauses three promoted aeroapi requests; the replacement batch (up to
        // 3, but only two requests remain once aeroapi is excluded entirely) reaches both free-source
        // requests instead of pausing three more of the four still-untried aeroapi requests.
        Assert.Equal(
            new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 5, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0, Paused: 3),
            result);
        Assert.Empty(aeroApi.Lookups);

        await using var verify = CreateDbContext();
        var requests = await verify.VerificationRequests.AsNoTracking().ToDictionaryAsync(r => r.Id);
        Assert.Equal(VerificationRequestStatus.Verified, requests[lhrRequest].Status);
        Assert.Equal(VerificationRequestStatus.Verified, requests[cdgRequest].Status);
        Assert.True((await verify.Airports.AsNoTracking().SingleAsync(a => a.Id == lhr.Id)).IsVerified);
        Assert.True((await verify.Airports.AsNoTracking().SingleAsync(a => a.Id == cdg.Id)).IsVerified);
        // Both free-source requests were actually claimed and looked up, not merely left alone by
        // accident -- real airports are seeded, so (unlike an unseeded request, which would end
        // Error "Entity no longer exists" without ever reaching the source) this is only possible if
        // the replacement claim reached them.
        Assert.Equal(2, ourAirports.Lookups.Count);

        // All seven aeroapi requests -- the three paused, and the four the replacement claim never
        // even touched -- are left exactly as they were: still Pending, no attempt, no error.
        Assert.Equal(7, oldAdminAeroApiIds.Count);
        Assert.All(oldAdminAeroApiIds, id =>
        {
            var r = requests[id];
            Assert.Equal(VerificationRequestStatus.Pending, r.Status);
            Assert.Equal(0, r.Attempts);
            Assert.Null(r.Error);
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>The job's services as the API host wires them, minus what a cycle doesn't touch.</summary>
    private ServiceProvider BuildJob(
        IVerificationSource[] sources, int maxAttempts = 3, int batchSize = 10,
        ProviderBudgetState budgetState = ProviderBudgetState.Normal)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddSingleton<TimeProvider>(_time);
        // IdleRelease zero: a source no open request uses is released after the cycle, as these tests expect.
        services.AddSingleton<IVerificationJobOptions>(new JobOptions(BatchSize: batchSize, MaxAttempts: maxAttempts, IdleRelease: TimeSpan.Zero));
        foreach (var source in sources)
            services.AddSingleton(source);
        services.AddSingleton<IVerificationSourceRegistry, VerificationSourceRegistry>();
        // Normal by default: most of these tests are not about the budget gate (#401 task 2,
        // Application-level tests own that); a fixed guard just satisfies
        // VerificationRequestProcessor's DI dependency without ever reaching AeroAPI. The #428
        // replacement-claim tests and the #419/#401 interaction test below pass Exhausted to
        // exercise the real pause-and-replace path.
        services.AddSingleton<IProviderBudgetGuard>(new FixedBudgetGuard(budgetState));

        // Program.cs's DbContext registration: the audit interceptor reads the scope's IAdminContext.
        services.AddScoped<AdminAuditInterceptor>();
        services.AddDbContext<FlightAssistantDbContext>((sp, options) => options
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(sp.GetRequiredService<AdminAuditInterceptor>()));
        services.AddScoped<IAirportRepository, AirportRepository>();
        services.AddScoped<IAirlineRepository, AirlineRepository>();
        services.AddScoped<ICountryRepository, CountryRepository>();
        services.AddScoped<IVerificationRequestRepository, VerificationRequestRepository>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<VerificationCycleResult> RunCycleAsync(ServiceProvider job, CancellationToken ct = default)
    {
        using var cycleScope = job.CreateScope();
        return await cycleScope.ServiceProvider.GetRequiredService<IVerificationCycleRunner>().RunCycleAsync(ct);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using var db = CreateDbContext();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private async Task<long> EnqueueAsync(
        VerificationEntityType entityType, string key, string source, DateTime? requestedUtc = null,
        VerificationTrigger trigger = VerificationTrigger.Admin)
    {
        await using var db = CreateDbContext();
        var request = VerificationRequest.Create(
            entityType, key, new[] { source }, trigger, "admin-oid", 0m, requestedUtc ?? T0.AddHours(-1));
        db.VerificationRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private VerificationRequestRepository AnotherWorker(FlightAssistantDbContext db, DateTime at) =>
        new(db, new FakeTimeProvider(new DateTimeOffset(at)), new JobOptions(BatchSize: 10, MaxAttempts: 3, IdleRelease: TimeSpan.Zero));

    private async Task<IReadOnlyList<VerificationRequest>> ClaimOnAnotherWorkerAsync(DateTime at, int batchSize)
    {
        await using var db = CreateDbContext();
        return await AnotherWorker(db, at).ClaimBatchAsync(batchSize, at);
    }

    private async Task<VerificationStuckResetResult> ResetOnAnotherWorkerAsync(DateTime at)
    {
        await using var db = CreateDbContext();
        return await AnotherWorker(db, at).ResetStuckRunningAsync(at - VerificationCycleRunner.StuckAfter, maxAttempts: 3, at);
    }

    private async Task AssertPendingAsync(long requestId, int attempts, DateTime nextAttemptUtc)
    {
        await using var verify = CreateDbContext();
        var row = await verify.VerificationRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        Assert.Equal(VerificationRequestStatus.Pending, row.Status);
        Assert.Equal(attempts, row.Attempts);
        Assert.Equal(nextAttemptUtc, row.NextAttemptUtc);
        Assert.Null(row.StartedUtc);
        Assert.Null(row.CompletedUtc);
    }

    private void AssertVerifiedBySystem(IVerifiable entity, string source)
    {
        Assert.True(entity.IsVerified);
        Assert.Equal("system", entity.VerifiedBy);
        Assert.Equal(source, entity.VerificationSource);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, entity.VerifiedAt);
        Assert.Null(entity.VerificationIssue);
    }

    private static void AssertNeedsAttention(IVerifiable entity, string issue)
    {
        Assert.False(entity.IsVerified);
        Assert.Null(entity.VerifiedBy);
        Assert.Null(entity.VerificationSource);
        Assert.Equal(issue, entity.VerificationIssue);
    }

    private static string Key(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static VerificationSourceFetchResult Found(IReadOnlyDictionary<string, string?> values) =>
        VerificationSourceFetchResult.Found(values);

    /// <summary>Job settings for a test. <see cref="DefaultSourcesFor"/> mirrors
    /// <c>VerificationOptions</c>'s own defaults: no test in this file is about the flight-tracking
    /// trigger (#410/#418) that reads <see cref="DefaultSourcesFor"/> or <see cref="ErrorCooldown"/>.</summary>
    private sealed record JobOptions(
        int BatchSize, int MaxAttempts, TimeSpan IdleRelease, int StaleAfterDays = 180, TimeSpan ErrorCooldown = default)
        : IVerificationJobOptions
    {
        /// <summary>Not a positional parameter: a <see cref="TimeSpan"/> default must be constant, and
        /// <c>TimeSpan.FromMinutes(10)</c> isn't one. Matches <c>VerificationOptions</c>'s own default
        /// (#419). A test that needs a different age sets it with an object initializer.</summary>
        public TimeSpan ClaimPromotionAge { get; init; } = TimeSpan.FromMinutes(10);

        public IReadOnlyList<string> DefaultSourcesFor(VerificationEntityType entityType) => entityType switch
        {
            VerificationEntityType.Airport => new[] { "ourairports" },
            VerificationEntityType.Airline => new[] { "aeroapi" },
            VerificationEntityType.Country => new[] { "mledoze" },
            _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unknown entity type.")
        };
    }

    /// <summary>A budget guard that always reports the same state -- these tests are not about the
    /// budget gate (#401 task 2), just its DI dependency on <c>VerificationRequestProcessor</c>.</summary>
    private sealed class FixedBudgetGuard(ProviderBudgetState state) : IProviderBudgetGuard
    {
        public Task<ProviderBudgetState> GetStateAsync(CancellationToken ct = default) => Task.FromResult(state);
    }

    private sealed class CountingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("unavailable") });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeSource : IVerificationSource
    {
        private readonly Func<VerificationSourceLookupKey, Task<VerificationSourceFetchResult>> _lookup;

        public FakeSource(string name, VerificationEntityType supported, Func<VerificationSourceLookupKey, Task<VerificationSourceFetchResult>> lookup)
        {
            Name = name;
            SupportedEntityTypes = new[] { supported };
            _lookup = lookup;
        }

        public FakeSource(string name, VerificationEntityType supported, Func<VerificationSourceLookupKey, VerificationSourceFetchResult> lookup)
            : this(name, supported, key => Task.FromResult(lookup(key)))
        {
        }

        public string Name { get; }
        public decimal CostPerRowUsd => 0m;
        public IReadOnlyCollection<VerificationEntityType> SupportedEntityTypes { get; }
        public List<VerificationSourceLookupKey> Lookups { get; } = new();
        public int ReleaseCount { get; private set; }

        /// <summary>Like a real source: set by a lookup, cleared by a release; a test can set it to
        /// stand for a dataset an earlier cycle loaded.</summary>
        public bool HasCachedData { get; set; }

        public Task<VerificationSourceFetchResult> LookupAsync(
            VerificationEntityType entityType, VerificationSourceLookupKey key, CancellationToken ct = default)
        {
            Lookups.Add(key);
            HasCachedData = true;
            return _lookup(key);
        }

        /// <summary>Releases whatever the idle period: the idle rule is <c>VerificationDatasetCache</c>'s,
        /// covered by its own tests.</summary>
        public void ReleaseCache(TimeSpan idleRelease)
        {
            ReleaseCount++;
            HasCachedData = false;
        }
    }
}
