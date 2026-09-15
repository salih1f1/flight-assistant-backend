using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
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
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));

    public VerificationCycleRunnerTests()
    {
        // A sealed record gets no auto-value from NSubstitute; nothing is stuck unless a test says so.
        _requests.ResetStuckRunningAsync(default, default, default, default)
            .ReturnsForAnyArgs(new VerificationStuckResetResult(ReturnedToPending: 0, Abandoned: 0));
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
        services.AddSingleton<TimeProvider>(_time);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var cycleScope = provider.CreateScope();
        return await cycleScope.ServiceProvider.GetRequiredService<IVerificationCycleRunner>().RunCycleAsync(ct);
    }

    private VerificationRequest[] ClaimAirports(params (long Id, string Key)[] requests)
    {
        var claimed = requests
            .Select(r => ClaimedRequests.Create(r.Id, VerificationEntityType.Airport, r.Key, new[] { "ourairports" }, ClaimedAt))
            .ToArray();
        _requests.ClaimBatchAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(claimed);
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
        _requests.ClaimBatchAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<VerificationRequest>());

        var result = await RunCycleAsync(new FakeVerificationSourceRegistry(), new FakeJobOptions(BatchSize: 7, MaxAttempts: 4));

        Assert.Equal(new VerificationCycleResult(StuckReset: 2, StuckAbandoned: 1, RequestsProcessed: 0, RequestsFailed: 0, ClaimsLost: 0, FailuresUnrecorded: 0), result);
        Received.InOrder(() =>
        {
            _requests.ResetStuckRunningAsync(Now.AddMinutes(-10), 4, Now, Arg.Any<CancellationToken>());
            _requests.ClaimBatchAsync(7, Now, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task RunCycleAsync_ReleasesTheCacheOfEverySourceNoOpenRequestUses()
    {
        ClaimAirports();
        _requests.AnyOpenRequestUsesSourceAsync("still-used", Arg.Any<CancellationToken>()).Returns(true);
        var unused = new FakeVerificationSource("unused", VerificationEntityType.Airport, VerificationSourceFetchResult.NotFound);
        var stillUsed = new FakeVerificationSource("still-used", VerificationEntityType.Airport, VerificationSourceFetchResult.NotFound);
        var registry = new FakeVerificationSourceRegistry(unused, stillUsed);

        await RunCycleAsync(registry);

        Assert.Equal(new[] { "still-used" }, Assert.Single(registry.ReleaseCalls));
        Assert.Equal(1, unused.ReleaseCount);
        Assert.Equal(0, stillUsed.ReleaseCount);
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
        _requests.AnyOpenRequestUsesSourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<bool>(cause));

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
