using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Verification;

/// <summary>
/// <see cref="VerificationCycleRunner"/> composed the way the job composes it: <c>AddApplication</c>
/// registrations in a real service provider, one scope per cycle and per request, with substitute
/// repositories shared across those scopes. The PostgreSQL cycle test
/// (<c>VerificationCyclePostgresTests</c>) covers the same runner against real persistence and auditing.
/// </summary>
public class VerificationCycleRunnerTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ClaimedAt = Now;

    private readonly IVerificationRequestRepository _requests = Substitute.For<IVerificationRequestRepository>();
    private readonly IAirportRepository _airports = Substitute.For<IAirportRepository>();
    private readonly IAirlineRepository _airlines = Substitute.For<IAirlineRepository>();
    private readonly ICountryRepository _countries = Substitute.For<ICountryRepository>();
    private readonly FakeProviderBudgetGuard _budget = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));

    public VerificationCycleRunnerTests()
    {
        // A sealed record gets no auto-value from NSubstitute; nothing is stuck unless a test says so.
        _requests.ResetStuckRunningAsync(default, default, default, default)
            .ReturnsForAnyArgs(new VerificationStuckResetResult(ReturnedToPending: 0, Abandoned: 0));
        _requests.GetSourceNamesInUseAsync(default).ReturnsForAnyArgs(Array.Empty<string>());
    }

    private static Airport Airport(string iata) => Domain.ReferenceData.Entities.Airport.Create(iata, null, $"{iata} Airport", "City", "GB");

    private async Task<VerificationCycleResult> RunCycleAsync(
        IVerificationSourceRegistry registry, FakeJobOptions? options = null, CancellationToken ct = default)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(_requests);
        services.AddSingleton(_airports);
        services.AddSingleton(_airlines);
        services.AddSingleton(_countries);
        services.AddSingleton(registry);
        services.AddSingleton<IVerificationJobOptions>(options ?? new FakeJobOptions());
        services.AddSingleton<IProviderBudgetGuard>(_budget);
        services.AddSingleton<TimeProvider>(_time);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var cycleScope = provider.CreateScope();
        return await cycleScope.ServiceProvider.GetRequiredService<IVerificationCycleRunner>().RunCycleAsync(ct);
    }

    private VerificationRequest[] ClaimAirports(params (long Id, string Key)[] requests) =>
        ClaimAirportsWithSources(requests.Select(r => (r.Id, r.Key, new[] { "ourairports" })).ToArray());

    private VerificationRequest[] ClaimAirportsWithSources(params (long Id, string Key, string[] Sources)[] requests)
    {
        var claimed = requests
            .Select(r => ClaimedRequests.Create(r.Id, VerificationEntityType.Airport, r.Key, r.Sources, ClaimedAt))
            .ToArray();
        // Any exclude list: most tests here don't exercise G1's replacement claim, so both the
        // initial call (excludeIds null) and a hypothetical replacement call return the same batch.
        _requests.ClaimBatchAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<long>?>(), Arg.Any<CancellationToken>())
            .Returns(claimed);
        foreach (var request in claimed)
            _requests.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        return claimed;
    }

    /// <summary>
    /// G1 (#401 final fix): sets up two distinct <c>ClaimBatchAsync</c> calls -- the initial one
    /// (<c>excludeIds</c> null) returning <paramref name="firstBatch"/>, and the replacement one
    /// (<c>excludeIds</c> matching exactly <paramref name="expectedExcludedIds"/>) returning
    /// <paramref name="secondBatch"/>. Registered after <see cref="ClaimAirportsWithSources"/>'s
    /// generic any-args setup would be, so these more specific matchers win for calls that match both.
    /// </summary>
    private VerificationRequest[] ClaimTwoBatches(
        (long Id, string Key, string[] Sources)[] firstBatch,
        long[] expectedExcludedIds,
        (long Id, string Key, string[] Sources)[] secondBatch)
    {
        var first = firstBatch.Select(r => ClaimedRequests.Create(r.Id, VerificationEntityType.Airport, r.Key, r.Sources, ClaimedAt)).ToArray();
        var second = secondBatch.Select(r => ClaimedRequests.Create(r.Id, VerificationEntityType.Airport, r.Key, r.Sources, ClaimedAt)).ToArray();

        _requests.ClaimBatchAsync(Arg.Any<int>(), Arg.Any<DateTime>(), null, Arg.Any<CancellationToken>()).Returns(first);
        _requests.ClaimBatchAsync(
                Arg.Any<int>(), Arg.Any<DateTime>(),
                Arg.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(expectedExcludedIds)),
                Arg.Any<CancellationToken>())
            .Returns(second);

        var claimed = first.Concat(second).ToArray();
        foreach (var request in claimed)
            _requests.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        return claimed;
    }

    private static FakeVerificationSource MatchingOurAirports() =>
        new("ourairports", VerificationEntityType.Airport,
            (key, _) => Task.FromResult(VerificationSourceFetchResult.Found(VerificationCheckedColumns.Project(Airport(key.Iata!)))));

    /// <summary>The first request's entity load throws. The exception is swallowed and recorded as a
    /// source error for that request (from a fresh scope), and the second request is still verified.</summary>
    [Fact]
    public async Task RunCycleAsync_OneRequestThrows_TheRestOfTheBatchIsStillProcessed_AndTheThrowIsASourceError()
    {
        var claimed = ClaimAirports((1, "11"), (2, "22"));
        var throwing = claimed[0];
        var healthy = claimed[1];
        _airports.GetByIdAsync(11, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Airport?>(new InvalidOperationException("database hiccup")));
        var airport = Airport("BBB");
        _airports.GetByIdAsync(22, Arg.Any<CancellationToken>()).Returns(airport);

        var result = await RunCycleAsync(new FakeVerificationSourceRegistry(MatchingOurAirports()));

        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 2, RequestsFailed: 1, ClaimsLost: 0, FailuresUnrecorded: 0), result);

        Assert.Equal(VerificationRequestStatus.Pending, throwing.Status);
        Assert.Equal(1, throwing.Attempts);
        Assert.Equal(Now.AddMinutes(1), throwing.NextAttemptUtc);
        Assert.Equal("Unexpected error (InvalidOperationException); see logs", throwing.Error);

        Assert.Equal(VerificationRequestStatus.Verified, healthy.Status);
        Assert.True(airport.IsVerified);
    }

    [Fact]
    public async Task RunCycleAsync_RecordingTheFailureAlsoThrows_TheBatchStillFinishes_AndCountsItFailedAndUnrecorded()
    {
        var claimed = ClaimAirports((1, "11"), (2, "22"));
        _requests.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromResult<VerificationRequest?>(claimed[0]),
            _ => Task.FromException<VerificationRequest?>(new TimeoutException("database gone")));
        _airports.GetByIdAsync(11, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Airport?>(new InvalidOperationException("database hiccup")));
        _airports.GetByIdAsync(22, Arg.Any<CancellationToken>()).Returns(Airport("BBB"));

        var result = await RunCycleAsync(new FakeVerificationSourceRegistry(MatchingOurAirports()));

        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 2, RequestsFailed: 1, ClaimsLost: 0, FailuresUnrecorded: 1), result);
        // Nothing was saved for the first request: it is still Running, with no attempt counted.
        Assert.Equal(VerificationRequestStatus.Running, claimed[0].Status);
        Assert.Equal(0, claimed[0].Attempts);
        Assert.Equal(VerificationRequestStatus.Verified, claimed[1].Status);
    }

    [Fact]
    public async Task RunCycleAsync_ResetsStuckRequestsOlderThanTenMinutesWithMaxAttempts_ThenClaimsBatchSize()
    {
        _requests.ResetStuckRunningAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new VerificationStuckResetResult(ReturnedToPending: 2, Abandoned: 1));
        _requests.ClaimBatchAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<long>?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<VerificationRequest>());

        var result = await RunCycleAsync(new FakeVerificationSourceRegistry(), new FakeJobOptions(BatchSize: 7, MaxAttempts: 4));

        Assert.Equal(new VerificationCycleResult(StuckReset: 2, StuckAbandoned: 1, RequestsProcessed: 0, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0), result);
        Received.InOrder(() =>
        {
            _requests.ResetStuckRunningAsync(Now.AddMinutes(-10), 4, Now, Arg.Any<CancellationToken>());
            _requests.ClaimBatchAsync(7, Now, null, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task RunCycleAsync_WhenASourceHoldsData_AsksForTheSourcesInUseOnce_AndReleasesEverySourceNoOpenRequestUses()
    {
        ClaimAirports();
        _requests.GetSourceNamesInUseAsync(Arg.Any<CancellationToken>()).Returns(new[] { "still-used", "not-registered" });
        var unused = new FakeVerificationSource("unused", VerificationEntityType.Airport, VerificationSourceFetchResult.NotFound) { HasCachedData = true };
        var stillUsed = new FakeVerificationSource("still-used", VerificationEntityType.Airport, VerificationSourceFetchResult.NotFound) { HasCachedData = true };
        var registry = new FakeVerificationSourceRegistry(unused, stillUsed);

        await RunCycleAsync(registry);

        await _requests.Received(1).GetSourceNamesInUseAsync(Arg.Any<CancellationToken>());
        Assert.Equal(new[] { "still-used", "not-registered" }, Assert.Single(registry.ReleaseCalls).SourceNamesInUse);
        Assert.Equal(1, unused.ReleaseCount);
        Assert.Equal(0, stillUsed.ReleaseCount);
    }

    /// <summary>#378: the release hands the registry the configured idle period, so an unused source's
    /// dataset is kept that long after its last lookup.</summary>
    [Fact]
    public async Task RunCycleAsync_ReleasesWithTheConfiguredIdlePeriod()
    {
        ClaimAirports();
        var cached = new FakeVerificationSource("ourairports", VerificationEntityType.Airport, VerificationSourceFetchResult.NotFound) { HasCachedData = true };
        var registry = new FakeVerificationSourceRegistry(cached);

        await RunCycleAsync(registry, new FakeJobOptions(IdleRelease: TimeSpan.FromMinutes(45)));

        Assert.Equal(TimeSpan.FromMinutes(45), Assert.Single(registry.ReleaseCalls).IdleRelease);
    }

    /// <summary>Final fix wave, C3: with nothing claimed and no source holding a dataset or a failure,
    /// releasing has nothing to drop, so the cycle doesn't ask the queue which sources are in use.</summary>
    [Fact]
    public async Task RunCycleAsync_AnIdleQueueWithNothingCached_RunsNoSourcesInUseQuery()
    {
        ClaimAirports();
        var registry = new FakeVerificationSourceRegistry(
            new FakeVerificationSource("ourairports", VerificationEntityType.Airport, VerificationSourceFetchResult.NotFound),
            new FakeVerificationSource("mledoze", VerificationEntityType.Country, VerificationSourceFetchResult.NotFound));

        var result = await RunCycleAsync(registry);

        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 0, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0), result);
        await _requests.DidNotReceive().GetSourceNamesInUseAsync(Arg.Any<CancellationToken>());
        Assert.Empty(registry.ReleaseCalls);
    }

    [Fact]
    public async Task RunCycleAsync_ALostClaim_IsCountedApartFromFailures_AndNotRecordedAsASourceError()
    {
        ClaimAirports((1, "11"));
        _airports.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(Airport("AAA"));
        _requests.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new VerificationClaimLostException(new[] { 1L }, new Exception("xmin"))));

        var result = await RunCycleAsync(new FakeVerificationSourceRegistry(MatchingOurAirports()));

        Assert.Equal(new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 1, RequestsFailed: 0, ClaimsLost: 1, FailuresUnrecorded: 0), result);
        await _requests.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycleAsync_CancelledWhileProcessing_Propagates_WithoutRecordingASourceError()
    {
        var claimed = ClaimAirports((1, "11"), (2, "22"));
        _airports.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Airport("AAA"));
        using var cts = new CancellationTokenSource();
        var cancelling = new FakeVerificationSource("ourairports", VerificationEntityType.Airport, (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(VerificationSourceFetchResult.NotFound);
        });
        var registry = new FakeVerificationSourceRegistry(cancelling);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunCycleAsync(registry, ct: cts.Token));

        Assert.Single(cancelling.Lookups);
        Assert.All(claimed, r => Assert.Equal(0, r.Attempts));
        await _requests.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Empty(registry.ReleaseCalls);
        // Both claims are unfinished (the first was interrupted): both go back to Pending.
        await _requests.Received(1).ReturnClaimsToPendingAsync(
            Arg.Is<IReadOnlyCollection<VerificationClaim>>(c => c.Select(x => x.RequestId).SequenceEqual(new[] { 1L, 2L })),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Final fix wave, C4: a restart mid-batch must not spend an attempt on every claim the
    /// cycle never reached. The finished request keeps its outcome; the rest go back to Pending, on a
    /// token of their own (the cycle's is already cancelled).</summary>
    [Fact]
    public async Task RunCycleAsync_CancelledAfterTheFirstRequest_ReturnsOnlyTheUnfinishedClaimsToPending_OnATokenOfItsOwn()
    {
        var claimed = ClaimAirports((1, "11"), (2, "22"), (3, "33"));
        _airports.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Airport("AAA"));
        using var cts = new CancellationTokenSource();
        var stopsTheHost = new FakeVerificationSource("ourairports", VerificationEntityType.Airport, (_, _) =>
        {
            cts.Cancel(); // shutdown starts during the first lookup, which still completes
            return Task.FromResult(VerificationSourceFetchResult.NotFound);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunCycleAsync(new FakeVerificationSourceRegistry(stopsTheHost), ct: cts.Token));

        Assert.Equal(VerificationRequestStatus.NotFound, claimed[0].Status);
        Assert.Single(stopsTheHost.Lookups);
        await _requests.Received(1).ReturnClaimsToPendingAsync(
            Arg.Is<IReadOnlyCollection<VerificationClaim>>(c =>
                c.SequenceEqual(new[] { new VerificationClaim(2, ClaimedAt), new VerificationClaim(3, ClaimedAt) })),
            Arg.Is<CancellationToken>(t => t.CanBeCanceled && !t.IsCancellationRequested));
    }

    [Fact]
    public async Task RunCycleAsync_CancelledAndReturningTheClaimsFails_StillPropagatesTheCancellation()
    {
        ClaimAirports((1, "11"));
        _airports.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Airport("AAA"));
        _requests.ReturnClaimsToPendingAsync(Arg.Any<IReadOnlyCollection<VerificationClaim>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<int>(new TimeoutException("database gone")));
        using var cts = new CancellationTokenSource();
        var cancelling = new FakeVerificationSource("ourairports", VerificationEntityType.Airport, (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(VerificationSourceFetchResult.NotFound);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunCycleAsync(new FakeVerificationSourceRegistry(cancelling), ct: cts.Token));

        await _requests.Received(1).ReturnClaimsToPendingAsync(
            Arg.Any<IReadOnlyCollection<VerificationClaim>>(), Arg.Any<CancellationToken>());
    }

    // ── AeroAPI budget gate (R4/R5, #401 task 2) ────────────────────────────

    /// <summary>
    /// Decision 4: the first gated request finds the budget Exhausted and pauses; the runner then
    /// stops asking the guard for the rest of the cycle, so the two later aeroapi-only requests pause
    /// on that knowledge alone -- one guard read total, not three, and neither request's source is
    /// ever looked up. G1: a queue that is nothing but gated requests still claims exactly one
    /// (empty) replacement batch and ends cleanly -- never a further, third claim call.
    /// </summary>
    [Fact]
    public async Task RunCycleAsync_AeroApiBudgetExhausted_PausesEveryGatedRequest_AskingTheGuardOnlyOnce_AndClaimsOneEmptyReplacementBatch()
    {
        _budget.State = ProviderBudgetState.Exhausted;
        var claimed = ClaimTwoBatches(
            firstBatch: new (long, string, string[])[] { (1, "11", new[] { "aeroapi" }), (2, "22", new[] { "aeroapi" }), (3, "33", new[] { "aeroapi" }) },
            expectedExcludedIds: new[] { 1L, 2L, 3L },
            secondBatch: Array.Empty<(long, string, string[])>());
        var aeroApi = new FakeVerificationSource("aeroapi", VerificationEntityType.Airport, VerificationSourceFetchResult.NotFound);

        var result = await RunCycleAsync(new FakeVerificationSourceRegistry(aeroApi));

        Assert.Equal(
            new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 3, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0, Paused: 3),
            result);
        Assert.Equal(1, _budget.CallCount);
        Assert.Empty(aeroApi.Lookups);
        Assert.All(claimed, r =>
        {
            Assert.Equal(VerificationRequestStatus.Running, r.Status);
            Assert.Equal(0, r.Attempts);
            Assert.Null(r.Error);
        });
        // "Don't loop" (G1): the initial claim plus exactly one replacement, never a third attempt
        // even though the replacement itself came back with nothing claimable.
        await _requests.Received(2).ClaimBatchAsync(
            Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<long>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Decision 2: a request naming only free sources keeps processing normally even once the
    /// cycle has paused on an earlier, aeroapi-naming request -- gating follows the source named, not
    /// the cycle's paused flag.</summary>
    [Fact]
    public async Task RunCycleAsync_AeroApiBudgetExhausted_ARequestNamingOnlyFreeSources_StillProcesses()
    {
        _budget.State = ProviderBudgetState.Exhausted;
        var claimed = ClaimTwoBatches(
            firstBatch: new (long, string, string[])[] { (1, "11", new[] { "aeroapi" }), (2, "22", new[] { "ourairports" }), (3, "33", new[] { "aeroapi" }) },
            expectedExcludedIds: new[] { 1L, 3L },
            secondBatch: Array.Empty<(long, string, string[])>());
        var airport = Airport("BBB");
        _airports.GetByIdAsync(22, Arg.Any<CancellationToken>()).Returns(airport);
        var aeroApi = new FakeVerificationSource("aeroapi", VerificationEntityType.Airport, VerificationSourceFetchResult.NotFound);
        var ourAirports = MatchingOurAirports();

        var result = await RunCycleAsync(new FakeVerificationSourceRegistry(aeroApi, ourAirports));

        Assert.Equal(
            new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 3, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0, Paused: 2),
            result);
        Assert.Equal(VerificationRequestStatus.Running, claimed[0].Status); // paused (first guard read)
        Assert.Equal(VerificationRequestStatus.Verified, claimed[1].Status); // free source: unaffected
        Assert.True(airport.IsVerified);
        Assert.Equal(VerificationRequestStatus.Running, claimed[2].Status); // paused (known-paused flag)
        Assert.Equal(1, _budget.CallCount);
        Assert.Empty(aeroApi.Lookups);
    }

    /// <summary>
    /// G1 (#401 final fix): the core scenario the finding names -- a batch full of gated requests
    /// with free-source work queued behind them. The initial claim pauses both aeroapi requests; the
    /// replacement claim (excluding exactly those ids) reaches the two ourairports requests that a
    /// naive single-claim cycle would never see until the month rolled over, and both complete.
    /// </summary>
    [Fact]
    public async Task RunCycleAsync_G1_AfterPausingTheGatedBatch_TheReplacementBatchReachesFreeSourceWorkBehindThem()
    {
        _budget.State = ProviderBudgetState.Exhausted;
        var claimed = ClaimTwoBatches(
            firstBatch: new (long, string, string[])[] { (1, "11", new[] { "aeroapi" }), (2, "22", new[] { "aeroapi" }) },
            expectedExcludedIds: new[] { 1L, 2L },
            secondBatch: new (long, string, string[])[] { (3, "33", new[] { "ourairports" }), (4, "44", new[] { "ourairports" }) });
        _airports.GetByIdAsync(33, Arg.Any<CancellationToken>()).Returns(Airport("CCC"));
        _airports.GetByIdAsync(44, Arg.Any<CancellationToken>()).Returns(Airport("DDD"));
        var aeroApi = new FakeVerificationSource("aeroapi", VerificationEntityType.Airport, VerificationSourceFetchResult.NotFound);
        var ourAirports = MatchingOurAirports();

        var result = await RunCycleAsync(new FakeVerificationSourceRegistry(aeroApi, ourAirports));

        Assert.Equal(
            new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 4, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0, Paused: 2),
            result);
        Assert.Equal(VerificationRequestStatus.Running, claimed[0].Status); // paused
        Assert.Equal(VerificationRequestStatus.Running, claimed[1].Status); // paused
        Assert.Equal(VerificationRequestStatus.Verified, claimed[2].Status); // reached via the replacement batch
        Assert.Equal(VerificationRequestStatus.Verified, claimed[3].Status); // reached via the replacement batch
        // One guard read total, across both batches -- the replacement's own aeroapi exposure is
        // zero here since nothing gated survived into it, but the flag would have covered it too.
        Assert.Equal(1, _budget.CallCount);
        Assert.Empty(aeroApi.Lookups);
    }

    [Fact]
    public async Task RunCycleAsync_AeroApiBudgetSoftLimit_DoesNotPause_TheGatedRequestStillCallsAeroApi()
    {
        _budget.State = ProviderBudgetState.SoftLimit;
        ClaimAirportsWithSources((1, "11", new[] { "aeroapi" }));
        _airports.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(Airport("AAA"));
        var aeroApi = new FakeVerificationSource("aeroapi", VerificationEntityType.Airport, VerificationSourceFetchResult.NotFound);

        var result = await RunCycleAsync(new FakeVerificationSourceRegistry(aeroApi));

        Assert.Equal(
            new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 1, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0, Paused: 0),
            result);
        Assert.Single(aeroApi.Lookups);
    }

    /// <summary>Releasing caches is the last step. If it throws, the batch's outcomes are already saved,
    /// so the cycle fails carrying that batch's counts (for the heartbeat) and the original cause.</summary>
    [Fact]
    public async Task RunCycleAsync_ReleaseThrowsAfterTheBatch_FailsTheCycle_CarryingTheBatchsCountsAndCause()
    {
        var claimed = ClaimAirports((1, "11"));
        var airport = Airport("AAA");
        _airports.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(airport);
        var cause = new InvalidOperationException("database gone");
        _requests.GetSourceNamesInUseAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyCollection<string>>(cause));

        var ex = await Assert.ThrowsAsync<VerificationCycleFailedException>(
            () => RunCycleAsync(new FakeVerificationSourceRegistry(MatchingOurAirports())));

        Assert.Equal(
            new VerificationCycleResult(StuckReset: 0, StuckAbandoned: 0, RequestsProcessed: 1, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0),
            ex.CompletedWork);
        Assert.Same(cause, ex.InnerException);
        Assert.Equal(VerificationRequestStatus.Verified, claimed[0].Status);
        Assert.True(airport.IsVerified);
    }
}
