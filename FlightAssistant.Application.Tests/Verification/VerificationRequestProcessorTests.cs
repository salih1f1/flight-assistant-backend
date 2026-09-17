using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Verification;

/// <summary>
/// <see cref="VerificationRequestProcessor"/>: one claimed request, every outcome of the Job table
/// (spec §4.2), against substitute repositories and fake sources. The request and the entity are real
/// domain objects, so the assertions read their state, not the calls made on them.
/// </summary>
public class VerificationRequestProcessorTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ClaimedAt = Now.AddSeconds(-5);
    private const long RequestId = 7;

    private readonly IVerificationRequestRepository _requests = Substitute.For<IVerificationRequestRepository>();
    private readonly IAirportRepository _airports = Substitute.For<IAirportRepository>();
    private readonly IAirlineRepository _airlines = Substitute.For<IAirlineRepository>();
    private readonly ICountryRepository _countries = Substitute.For<ICountryRepository>();
    private readonly FakeProviderBudgetGuard _budget = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));

    public VerificationRequestProcessorTests()
    {
        // The normal case: returning a paused claim to Pending touches exactly the one row it names.
        // G5's own test overrides this to 0 to prove the claim-lost path.
        _requests.ReturnClaimsToPendingAsync(Arg.Any<IReadOnlyCollection<VerificationClaim>>(), Arg.Any<CancellationToken>())
            .Returns(1);
    }

    private static Airport Heathrow() => Airport.Create("LHR", "EGLL", "London Heathrow", "London", "GB", 51.4706, -0.461941, 83);

    private static Country UnitedKingdom() => Country.Create("GB", "United Kingdom", "GBR", 826, "Europe", "Northern Europe");

    private VerificationRequestProcessor Processor(IVerificationSourceRegistry registry, int maxAttempts = 3) =>
        new(_requests, _airports, _airlines, _countries, registry, new FakeJobOptions(MaxAttempts: maxAttempts), _budget, _time,
            NullLogger<VerificationRequestProcessor>.Instance);

    private VerificationRequest ClaimedAirportRequest(string[] sources, int priorAttempts = 0)
    {
        var request = ClaimedRequests.Create(RequestId, VerificationEntityType.Airport, "42", sources, ClaimedAt, priorAttempts);
        _requests.GetByIdAsync(RequestId, Arg.Any<CancellationToken>()).Returns(request);
        return request;
    }

    private static VerificationClaim Claim => new(RequestId, ClaimedAt);

    private static FakeVerificationSource AirportSource(string name, VerificationSourceFetchResult answer) =>
        new(name, VerificationEntityType.Airport, answer);

    // ── All sources match ───────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_TheSourceMatches_VerifiesTheEntityAsSystemWithTheSourceName_AndTheRequest()
    {
        var airport = Heathrow();
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(airport);
        var request = ClaimedAirportRequest(new[] { "ourairports" });
        var ourAirports = AirportSource("ourairports", VerificationSourceFetchResult.Found(VerificationCheckedColumns.Project(Heathrow())));

        var outcome = await Processor(new FakeVerificationSourceRegistry(ourAirports)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Verified, outcome);
        Assert.True(airport.IsVerified);
        Assert.Equal("system", airport.VerifiedBy);
        Assert.Equal("ourairports", airport.VerificationSource);
        Assert.Equal(Now, airport.VerifiedAt);
        Assert.Null(airport.VerificationIssue);

        Assert.Equal(VerificationRequestStatus.Verified, request.Status);
        Assert.Equal(Now, request.CompletedUtc);
        Assert.Equal(0, request.Attempts);

        Assert.Equal(VerificationSourceLookupKey.ForAirport("LHR", "EGLL"), Assert.Single(ourAirports.Lookups));
        await _requests.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SeveralSourcesMatch_TheVerificationSourceJoinsThemWithPlus_InRequestOrder()
    {
        var airport = Heathrow();
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(airport);
        ClaimedAirportRequest(new[] { "zeta", "alpha" });
        var found = VerificationSourceFetchResult.Found(VerificationCheckedColumns.Project(Heathrow()));

        var outcome = await Processor(new FakeVerificationSourceRegistry(AirportSource("alpha", found), AirportSource("zeta", found)))
            .ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Verified, outcome);
        Assert.Equal("zeta+alpha", airport.VerificationSource);
    }

    [Fact]
    public async Task ProcessAsync_Country_MatchesByItsCode_AndIsVerified()
    {
        var country = UnitedKingdom();
        _countries.FindByCodeAsync("GB", Arg.Any<CancellationToken>()).Returns(country);
        var request = ClaimedRequests.Create(RequestId, VerificationEntityType.Country, "GB", new[] { "mledoze" }, ClaimedAt);
        _requests.GetByIdAsync(RequestId, Arg.Any<CancellationToken>()).Returns(request);
        var mledoze = new FakeVerificationSource("mledoze", VerificationEntityType.Country,
            VerificationSourceFetchResult.Found(VerificationCheckedColumns.Project(UnitedKingdom())));

        var outcome = await Processor(new FakeVerificationSourceRegistry(mledoze)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Verified, outcome);
        Assert.True(country.IsVerified);
        Assert.Equal("mledoze", country.VerificationSource);
        Assert.Equal(VerificationSourceLookupKey.ForCountry("GB"), Assert.Single(mledoze.Lookups));
        Assert.Equal(VerificationRequestStatus.Verified, request.Status);
    }

    [Fact]
    public async Task ProcessAsync_Airline_IsLoadedByItsId_AndLookedUpByItsCodes()
    {
        var airline = Airline.Create("BA", "BAW", "British Airways", "GB");
        _airlines.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(airline);
        var request = ClaimedRequests.Create(RequestId, VerificationEntityType.Airline, "42", new[] { "airlines" }, ClaimedAt);
        _requests.GetByIdAsync(RequestId, Arg.Any<CancellationToken>()).Returns(request);
        var source = new FakeVerificationSource("airlines", VerificationEntityType.Airline,
            VerificationSourceFetchResult.Found(VerificationCheckedColumns.Project(Airline.Create("BA", "BAW", "British Airways", "GB"))));

        var outcome = await Processor(new FakeVerificationSourceRegistry(source)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Verified, outcome);
        Assert.True(airline.IsVerified);
        // ForAirline, not ForAirport (#401) -- the processor now builds an airline's lookup key
        // through its own factory, even though the fields (and so the record equality) are identical.
        Assert.Equal(VerificationSourceLookupKey.ForAirline("BA", "BAW"), Assert.Single(source.Lookups));
    }

    // ── Mismatch and not found ──────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_TheSourceDisagrees_PassesTheSummaryToMarkVerificationFailed_AndStoresTheDifferences()
    {
        var airport = Heathrow();
        airport.Verify("admin-oid", VerificationSources.Manual, Now.AddDays(-3));
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(airport);
        var request = ClaimedAirportRequest(new[] { "ourairports" });

        var sourceRecord = new Dictionary<string, string?>(VerificationCheckedColumns.Project(Heathrow()))
        {
            [nameof(Airport.Name)] = "Heathrow",
            [nameof(Airport.City)] = "Hounslow"
        };
        var outcome = await Processor(new FakeVerificationSourceRegistry(
                AirportSource("ourairports", VerificationSourceFetchResult.Found(sourceRecord))))
            .ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Mismatch, outcome);
        Assert.False(airport.IsVerified);
        Assert.Null(airport.VerificationSource);
        Assert.Equal("Name, City differ (ourairports)", airport.VerificationIssue);

        Assert.Equal(VerificationRequestStatus.Mismatch, request.Status);
        Assert.Equal(Now, request.CompletedUtc);
        var expectedDifferences = new[]
        {
            new VerificationDifference(nameof(Airport.Name), "London Heathrow", "Heathrow", "ourairports"),
            new VerificationDifference(nameof(Airport.City), "London", "Hounslow", "ourairports")
        };
        Assert.Equal(VerificationJson.SerializeDifferences(expectedDifferences), request.DifferencesJson);
        Assert.Equal(
            VerificationJson.SerializeSourceValues(new Dictionary<string, IReadOnlyDictionary<string, string?>> { ["ourairports"] = sourceRecord }),
            request.SourceValuesJson);
    }

    [Fact]
    public async Task ProcessAsync_TheSourceHasNoRow_MarksTheEntityNotFoundInThatSource_AndTheRequestNotFound()
    {
        var airport = Heathrow();
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(airport);
        var request = ClaimedAirportRequest(new[] { "ourairports" });

        var outcome = await Processor(new FakeVerificationSourceRegistry(
                AirportSource("ourairports", VerificationSourceFetchResult.NotFound)))
            .ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.NotFound, outcome);
        Assert.False(airport.IsVerified);
        Assert.Equal("Not found in ourairports", airport.VerificationIssue);
        Assert.Equal(VerificationRequestStatus.NotFound, request.Status);
        Assert.Equal("{}", request.SourceValuesJson);
        Assert.Null(request.DifferencesJson);
        Assert.Equal(Now, request.CompletedUtc);
    }

    // ── Source errors ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_SourceErrorOnTheFirstAttempt_RetriesAfterOneMinute_AndLeavesTheEntityUnchanged()
    {
        var airport = Heathrow();
        var verifiedAt = Now.AddDays(-3);
        airport.Verify("admin-oid", VerificationSources.Manual, verifiedAt);
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(airport);
        var request = ClaimedAirportRequest(new[] { "ourairports" });

        var outcome = await Processor(new FakeVerificationSourceRegistry(
                AirportSource("ourairports", VerificationSourceFetchResult.SourceError("HTTP 503"))))
            .ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.RetryScheduled, outcome);
        Assert.Equal(VerificationRequestStatus.Pending, request.Status);
        Assert.Equal(1, request.Attempts);
        Assert.Equal(Now.AddMinutes(1), request.NextAttemptUtc);
        Assert.Equal("ourairports: HTTP 503", request.Error);
        Assert.Equal("ourairports", request.FailedSource); // #424: the upstream failure names its source
        Assert.Null(request.CompletedUtc);

        AssertStillManuallyVerified(airport, verifiedAt);
        await _requests.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SourceErrorOnTheSecondAttempt_RetriesAfterFiveMinutes()
    {
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(Heathrow());
        var request = ClaimedAirportRequest(new[] { "ourairports" }, priorAttempts: 1);

        var outcome = await Processor(new FakeVerificationSourceRegistry(
                AirportSource("ourairports", VerificationSourceFetchResult.SourceError("timeout"))))
            .ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.RetryScheduled, outcome);
        Assert.Equal(VerificationRequestStatus.Pending, request.Status);
        Assert.Equal(2, request.Attempts);
        Assert.Equal(Now.AddMinutes(5), request.NextAttemptUtc);
    }

    [Fact]
    public async Task ProcessAsync_SourceErrorOnALaterAttemptBelowMax_StillRetriesAfterFiveMinutes()
    {
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(Heathrow());
        var request = ClaimedAirportRequest(new[] { "ourairports" }, priorAttempts: 3);

        await Processor(new FakeVerificationSourceRegistry(
                AirportSource("ourairports", VerificationSourceFetchResult.SourceError("timeout"))), maxAttempts: 10)
            .ProcessAsync(Claim);

        Assert.Equal(VerificationRequestStatus.Pending, request.Status);
        Assert.Equal(4, request.Attempts);
        Assert.Equal(Now.AddMinutes(5), request.NextAttemptUtc);
    }

    [Fact]
    public async Task ProcessAsync_SourceErrorReachingMaxAttempts_CompletesAsError_WithTheEntityUnchanged()
    {
        var airport = Heathrow();
        var verifiedAt = Now.AddDays(-3);
        airport.Verify("admin-oid", VerificationSources.Manual, verifiedAt);
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(airport);
        var request = ClaimedAirportRequest(new[] { "ourairports" }, priorAttempts: 2);

        var outcome = await Processor(new FakeVerificationSourceRegistry(
                AirportSource("ourairports", VerificationSourceFetchResult.SourceError("HTTP 500"))), maxAttempts: 3)
            .ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Error, outcome);
        Assert.Equal(VerificationRequestStatus.Error, request.Status);
        Assert.Equal(3, request.Attempts);
        Assert.Equal("ourairports: HTTP 500", request.Error);
        Assert.Equal("ourairports", request.FailedSource); // #424: still attributed once it gives up
        Assert.Equal(Now, request.CompletedUtc);
        Assert.Null(request.NextAttemptUtc);

        AssertStillManuallyVerified(airport, verifiedAt);
    }

    [Fact]
    public async Task ProcessAsync_TheFirstSourceErrors_TheLaterSourcesAreNotLookedUp()
    {
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(Heathrow());
        ClaimedAirportRequest(new[] { "broken", "healthy" });
        var healthy = AirportSource("healthy", VerificationSourceFetchResult.NotFound);

        await Processor(new FakeVerificationSourceRegistry(
                AirportSource("broken", VerificationSourceFetchResult.SourceError("HTTP 429")), healthy))
            .ProcessAsync(Claim);

        Assert.Empty(healthy.Lookups);
    }

    /// <summary>
    /// #424 fix wave: the only other multi-source fetch-error test above (<c>TheFirstSourceErrors</c>)
    /// has its <em>first</em> source fail, so <c>request.SourceNames[0]</c> would name the failing
    /// source by coincidence there too. This one puts the healthy source first and the failing one
    /// second, so an attribution bug that names the first source instead of the one that actually
    /// failed shows up as a wrong <c>FailedSource</c>, not a passing test. Break-proof: replacing
    /// <c>source.Name</c> with <c>request.SourceNames[0]</c> at the <c>RecordSourceErrorAsync</c> call
    /// in <c>ProcessAsync</c> makes this fail.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_TheSecondSourceErrors_NamesIt_NotTheFirst()
    {
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(Heathrow());
        var request = ClaimedAirportRequest(new[] { "healthy", "broken" });
        var healthy = AirportSource("healthy", VerificationSourceFetchResult.NotFound);
        var broken = AirportSource("broken", VerificationSourceFetchResult.SourceError("HTTP 503"));

        var outcome = await Processor(new FakeVerificationSourceRegistry(healthy, broken)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.RetryScheduled, outcome);
        Assert.Equal("broken: HTTP 503", request.Error);
        Assert.Equal("broken", request.FailedSource); // #424 fix wave: names the source that actually failed
        Assert.Single(healthy.Lookups);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 5)]
    [InlineData(9, 5)]
    public void Backoff_IsOneMinuteAfterTheFirstAttempt_AndFiveAfterEveryLaterOne(int failedAttempts, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), VerificationRetryBackoff.After(failedAttempts));
    }

    // ── AeroAPI budget gate (R4/R5, #401 task 2) ──────────────────────────────

    /// <summary>
    /// Claims a request with two <em>prior</em> attempts already recorded, not a fresh one -- a fresh
    /// claim's <c>Attempts</c> is already 0, so "unchanged" and "reset to zero" would read identically.
    /// Only a request that walks in with a non-zero attempt count, a stored error and a next-attempt
    /// timestamp can prove the pause leaves all three exactly as they were (R5), rather than merely
    /// leaving zero as zero.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_AeroApiBudgetExhausted_PausesTheRequest_ExactlyAsItWas_NoAttemptNoErrorNoSave()
    {
        _budget.State = ProviderBudgetState.Exhausted;
        var request = ClaimedAirportRequest(new[] { "aeroapi" }, priorAttempts: 2);
        var priorAttempts = request.Attempts;
        var priorError = request.Error;
        var priorNextAttemptUtc = request.NextAttemptUtc;
        var aeroApi = AirportSource("aeroapi", VerificationSourceFetchResult.Found(VerificationCheckedColumns.Project(Heathrow())));

        var outcome = await Processor(new FakeVerificationSourceRegistry(aeroApi)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Paused, outcome);
        // Untouched: still the claimed Running request, and its prior attempt count, error and
        // next-attempt timestamp survive the pause exactly as they were (R5) -- no attempt counted,
        // no error recorded, nothing reset.
        Assert.Equal(VerificationRequestStatus.Running, request.Status);
        Assert.Equal(2, priorAttempts);
        Assert.Equal(priorAttempts, request.Attempts);
        Assert.Equal(priorError, request.Error);
        Assert.NotNull(request.Error);
        Assert.Equal(priorNextAttemptUtc, request.NextAttemptUtc);
        Assert.Null(request.CompletedUtc);
        Assert.Empty(aeroApi.Lookups);

        await _requests.Received(1).ReturnClaimsToPendingAsync(
            Arg.Is<IReadOnlyCollection<VerificationClaim>>(c => c.Single() == Claim), Arg.Any<CancellationToken>());
        await _requests.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ProviderBudgetState.Normal)]
    [InlineData(ProviderBudgetState.SoftLimit)]
    public async Task ProcessAsync_AeroApiBudgetNormalOrSoftLimit_ProceedsToFetchAeroApi(ProviderBudgetState state)
    {
        _budget.State = state;
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(Heathrow());
        ClaimedAirportRequest(new[] { "aeroapi" });
        var aeroApi = AirportSource("aeroapi", VerificationSourceFetchResult.Found(VerificationCheckedColumns.Project(Heathrow())));

        var outcome = await Processor(new FakeVerificationSourceRegistry(aeroApi)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Verified, outcome);
        Assert.Single(aeroApi.Lookups);
        await _requests.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _requests.DidNotReceive().ReturnClaimsToPendingAsync(
            Arg.Any<IReadOnlyCollection<VerificationClaim>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Decision 2: which source a request names decides whether the gate applies at all.
    /// The guard reporting Exhausted must not even be consulted for a request naming only free
    /// sources.</summary>
    [Fact]
    public async Task ProcessAsync_ARequestNamingOnlyFreeSources_IgnoresAnExhaustedBudget_AndNeverAsksTheGuard()
    {
        _budget.State = ProviderBudgetState.Exhausted;
        var airport = Heathrow();
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(airport);
        ClaimedAirportRequest(new[] { "ourairports" });
        var ourAirports = AirportSource("ourairports", VerificationSourceFetchResult.Found(VerificationCheckedColumns.Project(Heathrow())));

        var outcome = await Processor(new FakeVerificationSourceRegistry(ourAirports)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Verified, outcome);
        Assert.Equal(0, _budget.CallCount);
    }

    /// <summary>Decision 4: once the cycle already knows the budget paused, a gated request is paused
    /// on that flag alone -- the processor never reads the guard a second time.</summary>
    [Fact]
    public async Task ProcessAsync_AeroApiBudgetAlreadyKnownPausedThisCycle_Pauses_WithoutAskingTheGuardAgain()
    {
        _budget.State = ProviderBudgetState.Normal; // if the guard were asked, it would say proceed
        ClaimedAirportRequest(new[] { "aeroapi" });
        var aeroApi = AirportSource("aeroapi", VerificationSourceFetchResult.NotFound);

        var outcome = await Processor(new FakeVerificationSourceRegistry(aeroApi))
            .ProcessAsync(Claim, aeroApiBudgetPaused: true);

        Assert.Equal(VerificationProcessOutcome.Paused, outcome);
        Assert.Equal(0, _budget.CallCount);
        Assert.Empty(aeroApi.Lookups);
    }

    /// <summary>The known-paused flag from an earlier gated request in the same cycle must not leak
    /// into a later request that names only free sources.</summary>
    [Fact]
    public async Task ProcessAsync_AeroApiBudgetKnownPausedThisCycle_ARequestNamingOnlyFreeSources_StillProceeds()
    {
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(Heathrow());
        ClaimedAirportRequest(new[] { "ourairports" });
        var ourAirports = AirportSource("ourairports", VerificationSourceFetchResult.Found(VerificationCheckedColumns.Project(Heathrow())));

        var outcome = await Processor(new FakeVerificationSourceRegistry(ourAirports))
            .ProcessAsync(Claim, aeroApiBudgetPaused: true);

        Assert.Equal(VerificationProcessOutcome.Verified, outcome);
        Assert.Equal(0, _budget.CallCount);
    }

    /// <summary>G4 (final fix): <c>sources.Any(...)</c> is what makes a mixed request gated -- every
    /// other gate test here names <c>aeroapi</c> alone or a free source alone, so a request naming
    /// both together was unproven.</summary>
    [Fact]
    public async Task ProcessAsync_AeroApiBudgetExhausted_ARequestNamingAFreeSourceAndAeroApiTogether_Pauses()
    {
        _budget.State = ProviderBudgetState.Exhausted;
        var request = ClaimedAirportRequest(new[] { "ourairports", "aeroapi" });
        var ourAirports = AirportSource("ourairports", VerificationSourceFetchResult.Found(VerificationCheckedColumns.Project(Heathrow())));
        var aeroApi = AirportSource("aeroapi", VerificationSourceFetchResult.NotFound);

        var outcome = await Processor(new FakeVerificationSourceRegistry(ourAirports, aeroApi)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Paused, outcome);
        Assert.Equal(VerificationRequestStatus.Running, request.Status);
        Assert.Equal(0, request.Attempts);
        Assert.Empty(ourAirports.Lookups);
        Assert.Empty(aeroApi.Lookups);
    }

    /// <summary>Final fix G5: a zero-row update means the claim was lost -- reclaimed (by the
    /// stuck-request reset, or another claim) between the guard finding Exhausted and the pause being
    /// recorded -- so this is <c>ClaimLost</c>, not a pause that never actually happened.</summary>
    [Fact]
    public async Task ProcessAsync_AeroApiBudgetExhausted_TheReturnToPendingClaimsZeroRows_IsClaimLost_NotPaused()
    {
        _budget.State = ProviderBudgetState.Exhausted;
        ClaimedAirportRequest(new[] { "aeroapi" });
        _requests.ReturnClaimsToPendingAsync(Arg.Any<IReadOnlyCollection<VerificationClaim>>(), Arg.Any<CancellationToken>())
            .Returns(0);
        var aeroApi = AirportSource("aeroapi", VerificationSourceFetchResult.NotFound);

        var outcome = await Processor(new FakeVerificationSourceRegistry(aeroApi)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.ClaimLost, outcome);
    }

    /// <summary>Final fix G6: a database failure while returning the claim to Pending is caught here,
    /// not left to propagate -- the cycle runner treats any exception from <c>ProcessAsync</c> as
    /// unexpected and records it as a source error (attempts +1, error text, backoff), exactly what
    /// R5 forbids for a pause.</summary>
    [Fact]
    public async Task ProcessAsync_AeroApiBudgetExhausted_TheReturnToPendingThrows_StillReportsPaused_WithoutPropagating()
    {
        _budget.State = ProviderBudgetState.Exhausted;
        var request = ClaimedAirportRequest(new[] { "aeroapi" });
        _requests.ReturnClaimsToPendingAsync(Arg.Any<IReadOnlyCollection<VerificationClaim>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<int>(new TimeoutException("database gone")));
        var aeroApi = AirportSource("aeroapi", VerificationSourceFetchResult.NotFound);

        var outcome = await Processor(new FakeVerificationSourceRegistry(aeroApi)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Paused, outcome);
        Assert.Equal(VerificationRequestStatus.Running, request.Status);
        Assert.Equal(0, request.Attempts);
        Assert.Null(request.Error);
    }

    // ── Terminal errors ─────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_TheEntityWasDeleted_CompletesAsError_WithoutLookingUpAnySource()
    {
        var request = ClaimedAirportRequest(new[] { "ourairports" });
        var ourAirports = AirportSource("ourairports", VerificationSourceFetchResult.NotFound);

        var outcome = await Processor(new FakeVerificationSourceRegistry(ourAirports)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Error, outcome);
        Assert.Equal(VerificationRequestStatus.Error, request.Status);
        Assert.Equal("Entity no longer exists", request.Error);
        Assert.Null(request.FailedSource); // #424: a deleted entity isn't any source's fault
        Assert.Equal(0, request.Attempts);
        Assert.Equal(Now, request.CompletedUtc);
        Assert.Empty(ourAirports.Lookups);
    }

    /// <summary><c>aeroapi</c> has no source before step 9. With attempts to spare, a retry would put
    /// the request back to Pending; it must complete as Error at once, before any lookup, and before
    /// the registered source beside it is asked.</summary>
    [Fact]
    public async Task ProcessAsync_AnUnregisteredSource_CompletesAsErrorImmediately_NamingIt_WithoutRetrying()
    {
        var airport = Heathrow();
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(airport);
        var request = ClaimedAirportRequest(new[] { "ourairports", "aeroapi" });
        var ourAirports = AirportSource("ourairports", VerificationSourceFetchResult.Found(VerificationCheckedColumns.Project(Heathrow())));

        var outcome = await Processor(new FakeVerificationSourceRegistry(ourAirports), maxAttempts: 3).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Error, outcome);
        Assert.Equal(VerificationRequestStatus.Error, request.Status);
        Assert.Equal("Source 'aeroapi' is not registered.", request.Error);
        Assert.Equal("aeroapi", request.FailedSource); // #424: names the unregistered source itself
        Assert.Equal(0, request.Attempts);
        Assert.Null(request.NextAttemptUtc);
        Assert.Equal(Now, request.CompletedUtc);

        Assert.Empty(ourAirports.Lookups);
        Assert.False(airport.IsVerified);
        Assert.Null(airport.VerificationIssue);
        await _requests.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ASourceThatDoesNotSupportTheEntityType_CompletesAsErrorImmediately_NamingIt()
    {
        var request = ClaimedRequests.Create(RequestId, VerificationEntityType.Country, "GB", new[] { "ourairports" }, ClaimedAt);
        _requests.GetByIdAsync(RequestId, Arg.Any<CancellationToken>()).Returns(request);
        var ourAirports = AirportSource("ourairports", VerificationSourceFetchResult.NotFound);

        var outcome = await Processor(new FakeVerificationSourceRegistry(ourAirports)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Error, outcome);
        Assert.Equal("Source 'ourairports' does not support Country.", request.Error);
        Assert.Equal("ourairports", request.FailedSource); // #424: names the unsupported source
        Assert.Equal(0, request.Attempts);
        Assert.Empty(ourAirports.Lookups);
        await _countries.DidNotReceiveWithAnyArgs().FindByCodeAsync(default!, default);
    }

    /// <summary>
    /// #424 fix wave: the test above uses a single-source request, so
    /// <c>request.SourceNames[0]</c> would coincidentally name the same source that fails there too.
    /// This one puts a source that supports Country first and one that doesn't second, so a
    /// first-source shortcut would name the wrong source. Break-proof: replacing <c>name</c> with
    /// <c>request.SourceNames[0]</c> at this site's <c>CompleteAsErrorAsync</c> call in
    /// <c>ProcessAsync</c> makes this fail.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_TheSecondSourceDoesNotSupportTheEntityType_NamesIt_NotTheFirst()
    {
        var request = ClaimedRequests.Create(
            RequestId, VerificationEntityType.Country, "GB", new[] { "mledoze", "ourairports" }, ClaimedAt);
        _requests.GetByIdAsync(RequestId, Arg.Any<CancellationToken>()).Returns(request);
        var mledoze = new FakeVerificationSource("mledoze", VerificationEntityType.Country, VerificationSourceFetchResult.NotFound);
        var ourAirports = AirportSource("ourairports", VerificationSourceFetchResult.NotFound); // supports Airport, not Country

        var outcome = await Processor(new FakeVerificationSourceRegistry(mledoze, ourAirports)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.Error, outcome);
        Assert.Equal("Source 'ourairports' does not support Country.", request.Error);
        Assert.Equal("ourairports", request.FailedSource); // #424 fix wave: names the second source, not the first
        Assert.Empty(mledoze.Lookups);
    }

    // ── Lost claims ─────────────────────────────────────────────────────────

    public static TheoryData<string> NoLongerTheClaim => new() { "reset to Pending", "claimed again later", "gone" };

    [Theory]
    [MemberData(nameof(NoLongerTheClaim))]
    public async Task ProcessAsync_TheReloadedRequestIsNoLongerThisClaim_IsSkipped_WithoutLookupOrSave(string state)
    {
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(Heathrow());
        VerificationRequest? reloaded = state switch
        {
            "reset to Pending" => VerificationRequest.Create(VerificationEntityType.Airport, "42", new[] { "ourairports" },
                VerificationTrigger.Admin, "admin-oid", 0m, ClaimedAt.AddHours(-1)),
            "claimed again later" => ClaimedRequests.Create(RequestId, VerificationEntityType.Airport, "42", new[] { "ourairports" },
                ClaimedAt.AddMinutes(20)),
            _ => null
        };
        _requests.GetByIdAsync(RequestId, Arg.Any<CancellationToken>()).Returns(reloaded);
        var ourAirports = AirportSource("ourairports", VerificationSourceFetchResult.NotFound);

        var outcome = await Processor(new FakeVerificationSourceRegistry(ourAirports)).ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.ClaimLost, outcome);
        Assert.Empty(ourAirports.Lookups);
        Assert.Equal(0, reloaded?.Attempts ?? 0);
        await _requests.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_TheSaveReportsTheClaimLost_ReturnsClaimLost_WithoutRethrowingOrSavingAgain()
    {
        _airports.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(Heathrow());
        ClaimedAirportRequest(new[] { "ourairports" });
        _requests.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new VerificationClaimLostException(new[] { RequestId }, new Exception("xmin"))));

        var outcome = await Processor(new FakeVerificationSourceRegistry(
                AirportSource("ourairports", VerificationSourceFetchResult.Found(VerificationCheckedColumns.Project(Heathrow())))))
            .ProcessAsync(Claim);

        Assert.Equal(VerificationProcessOutcome.ClaimLost, outcome);
        await _requests.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Unexpected failures ─────────────────────────────────────────────────

    [Fact]
    public async Task RecordUnexpectedFailureAsync_RecordsASourceErrorNamingTheExceptionType()
    {
        var request = ClaimedAirportRequest(new[] { "ourairports" });

        var outcome = await Processor(new FakeVerificationSourceRegistry())
            .RecordUnexpectedFailureAsync(Claim, new InvalidOperationException("boom"));

        Assert.Equal(VerificationProcessOutcome.RetryScheduled, outcome);
        Assert.Equal(VerificationRequestStatus.Pending, request.Status);
        Assert.Equal(1, request.Attempts);
        Assert.Equal(Now.AddMinutes(1), request.NextAttemptUtc);
        Assert.Equal("Unexpected error (InvalidOperationException); see logs", request.Error);
        Assert.Null(request.FailedSource); // #424: not attributable to a source
        await _requests.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// #424: <see cref="VerificationRequestProcessor.RecordUnexpectedFailureAsync"/> travels through
    /// the same private <c>RecordSourceErrorAsync</c> helper the genuine upstream failure (the
    /// request's own source, faulted while fetching) uses -- so it would be very easy to thread the
    /// claimed request's source through by mistake and blame a healthy provider for a processor
    /// crash. The claimed request here names two sources precisely so an accidental "use the first
    /// (or only) source name" shortcut would still be caught by this assertion. Break-proof: passing
    /// any non-null source at the <c>RecordSourceErrorAsync</c> call inside
    /// <c>RecordUnexpectedFailureAsync</c> makes this fail.
    /// </summary>
    [Fact]
    public async Task RecordUnexpectedFailureAsync_NamesNoSource_EvenThoughTheRequestNamesTwo()
    {
        var request = ClaimedAirportRequest(new[] { "ourairports", "aeroapi" });

        var outcome = await Processor(new FakeVerificationSourceRegistry())
            .RecordUnexpectedFailureAsync(Claim, new InvalidOperationException("boom"));

        Assert.Equal(VerificationProcessOutcome.RetryScheduled, outcome);
        Assert.Null(request.FailedSource);
    }

    /// <summary>
    /// An EF or Npgsql message can carry a host and port, a constraint name or key values, and the
    /// <c>error</c> column is shown to admins in every environment. Only the exception's type may
    /// reach it, never any part of its message or of an inner exception's.
    /// </summary>
    [Fact]
    public async Task RecordUnexpectedFailureAsync_StoresOnlyTheExceptionType_NothingFromItsMessage()
    {
        var request = ClaimedAirportRequest(new[] { "ourairports" });
        var exception = new InvalidOperationException(
            "Failed to connect to db-prod.internal:5432; duplicate key value violates unique constraint " +
            "\"ux_verification_requests_open_entity\"; Key (entity_key)=(4242)",
            new TimeoutException("inner detail from 10.1.2.3"));

        await Processor(new FakeVerificationSourceRegistry()).RecordUnexpectedFailureAsync(Claim, exception);

        Assert.Equal("Unexpected error (InvalidOperationException); see logs", request.Error);
        foreach (var fragment in new[]
                 { "Failed to connect", "db-prod.internal", "5432", "ux_verification_requests_open_entity", "entity_key", "4242", "inner detail", "10.1.2.3", "TimeoutException" })
            Assert.DoesNotContain(fragment, request.Error);
    }

    private static void AssertStillManuallyVerified(Airport airport, DateTime verifiedAt)
    {
        Assert.True(airport.IsVerified);
        Assert.Equal("admin-oid", airport.VerifiedBy);
        Assert.Equal(VerificationSources.Manual, airport.VerificationSource);
        Assert.Equal(verifiedAt, airport.VerifiedAt);
        Assert.Null(airport.VerificationIssue);
    }
}
