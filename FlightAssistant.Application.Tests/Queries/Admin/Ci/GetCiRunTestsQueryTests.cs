using System.Text.Json;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Admin.Ci;
using FlightAssistant.Domain.Common.Exceptions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FlightAssistant.Application.Tests.Queries.Admin.Ci;

/// <summary>
/// The risk here is serving the wrong report: a stale "no report" for a run that has since uploaded
/// one, or an old attempt's report after a re-run. These tests pin what is cached, for how long, and
/// under which key.
/// </summary>
public class GetCiRunTestsQueryTests
{
    private const long RunId = 34761036906;

    private static readonly CiTestResultsArtifact Artifact =
        new(10319006519, Expired: false, new DateTimeOffset(2026, 9, 13, 13, 54, 19, TimeSpan.Zero));

    private static readonly CiTestReport Report = new(
        Commit: "1cc8f1a2966782782a2456e85d91eaa2fe0ff192",
        Ref: "development",
        Total: 2,
        Passed: 1,
        Failed: 1,
        Skipped: 0,
        DurationMs: 14,
        Suites:
        [
            new CiTestSuite("FlightAssistant.Application.Tests", 2, 1, 1, 0, 14,
            [
                new CiTestCase("MembershipNotFound_Throws", "UpdateLoyaltyMembershipCommandHandlerTests", "Passed", 2, ""),
                new CiTestCase("Handle_NewAirport_InsertsAndReturnsInsertedCount", "ImportAirportsCommandHandlerTests", "Failed", 12,
                    "Assert.Equal() Failure: Values differ"),
            ]),
        ]);

    private sealed class FakeCache : ICacheService
    {
        private readonly Dictionary<string, object?> _entries = [];
        public List<(string Key, TimeSpan? Ttl)> Writes { get; } = [];

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
            Task.FromResult(_entries.TryGetValue(key, out var value) ? (T?)value : default);

        public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpire = null, CancellationToken ct = default)
        {
            _entries[key] = value;
            Writes.Add((key, absoluteExpire));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }
    }

    private readonly ICiRunProvider _ci = Substitute.For<ICiRunProvider>();
    private readonly FakeCache _cache = new();
    private readonly GetCiRunTestsQueryHandler _handler;

    public GetCiRunTestsQueryTests()
    {
        _ci.FindTestResultsArtifactAsync(RunId, Arg.Any<CancellationToken>()).Returns(Artifact);
        _ci.GetTestReportAsync(Artifact.Id, Arg.Any<CancellationToken>()).Returns(Report);
        _handler = new GetCiRunTestsQueryHandler(_ci, _cache);
    }

    [Fact]
    public async Task Handle_FindsTheRunsArtifact_AndReturnsItsReport()
    {
        var report = await _handler.Handle(new GetCiRunTestsQuery(RunId), CancellationToken.None);

        Assert.Same(Report, report);
        await _ci.Received(1).GetTestReportAsync(Artifact.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CachesTheLookupForAMinute_AndTheReportForAnHour_UnderTheArtifactId()
    {
        await _handler.Handle(new GetCiRunTestsQuery(RunId), CancellationToken.None);

        Assert.Equal(
            new (string, TimeSpan?)[]
            {
                ($"ci:run-artifact:{RunId}", TimeSpan.FromSeconds(60)),
                ($"ci:test-report:{Artifact.Id}", TimeSpan.FromHours(1)),
            },
            _cache.Writes);
    }

    [Fact]
    public async Task Handle_DoesNotAskGitHubAgain_OnACacheHit()
    {
        await _handler.Handle(new GetCiRunTestsQuery(RunId), CancellationToken.None);
        var second = await _handler.Handle(new GetCiRunTestsQuery(RunId), CancellationToken.None);

        Assert.Same(Report, second);
        await _ci.Received(1).FindTestResultsArtifactAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _ci.Received(1).GetTestReportAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ARunWithNoReport_Is404_AndIsLookedUpAgainNextTime()
    {
        _ci.FindTestResultsArtifactAsync(RunId, Arg.Any<CancellationToken>()).Returns((CiTestResultsArtifact?)null);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _handler.Handle(new GetCiRunTestsQuery(RunId), CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("has no test report", ex.Message);
        Assert.Empty(_cache.Writes);

        // The run finishes and uploads its report: the next request must find it straight away.
        _ci.FindTestResultsArtifactAsync(RunId, Arg.Any<CancellationToken>()).Returns(Artifact);
        var report = await _handler.Handle(new GetCiRunTestsQuery(RunId), CancellationToken.None);

        Assert.Same(Report, report);
    }

    [Fact]
    public async Task Handle_AnExpiredReport_Is404_WithoutDownloadingIt()
    {
        _ci.FindTestResultsArtifactAsync(RunId, Arg.Any<CancellationToken>()).Returns(Artifact with { Expired = true });

        var ex = await Assert.ThrowsAsync<DomainException>(() => _handler.Handle(new GetCiRunTestsQuery(RunId), CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("expired", ex.Message);
        await _ci.DidNotReceive().GetTestReportAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AfterAReRun_ServesTheNewAttemptsReport_NotTheCachedOldOne()
    {
        await _handler.Handle(new GetCiRunTestsQuery(RunId), CancellationToken.None);

        // The lookup's minute is up, and the re-run has uploaded a new artifact.
        await _cache.RemoveAsync($"ci:run-artifact:{RunId}");
        var rerunArtifact = Artifact with { Id = 10319999999 };
        var rerunReport = Report with { Failed = 0, Passed = 2 };
        _ci.FindTestResultsArtifactAsync(RunId, Arg.Any<CancellationToken>()).Returns(rerunArtifact);
        _ci.GetTestReportAsync(rerunArtifact.Id, Arg.Any<CancellationToken>()).Returns(rerunReport);

        var report = await _handler.Handle(new GetCiRunTestsQuery(RunId), CancellationToken.None);

        Assert.Same(rerunReport, report);
    }

    [Fact]
    public async Task Handle_DoesNotCacheAFailedDownload()
    {
        _ci.GetTestReportAsync(Artifact.Id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new DomainException("CI results are temporarily unavailable.", 502));

        await Assert.ThrowsAsync<DomainException>(() => _handler.Handle(new GetCiRunTestsQuery(RunId), CancellationToken.None));

        Assert.DoesNotContain(_cache.Writes, w => w.Key.StartsWith("ci:test-report:"));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void Validator_RequiresAPositiveRunId(long runId, bool valid)
    {
        Assert.Equal(valid, new GetCiRunTestsQueryValidator().Validate(new GetCiRunTestsQuery(runId)).IsValid);
    }

    [Fact]
    public void CachedValues_SurviveAJsonRoundTrip_TheWayRedisCacheServiceStoresThem()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var artifact = JsonSerializer.Deserialize<CiTestResultsArtifact>(JsonSerializer.Serialize(Artifact, options), options);
        var report = JsonSerializer.Deserialize<CiTestReport>(JsonSerializer.Serialize(Report, options), options);

        Assert.Equal(Artifact, artifact);
        Assert.Equal(Report.Total, report!.Total);
        var failed = report.Suites.Single().Tests.Single(t => t.Outcome == "Failed");
        Assert.Equal(Report.Suites[0].Tests[1], failed);
    }
}
