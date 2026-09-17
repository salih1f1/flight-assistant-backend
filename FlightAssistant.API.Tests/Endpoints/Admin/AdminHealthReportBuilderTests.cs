using FlightAssistant.API.Endpoints.Admin;
using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.API.Tests.Endpoints.Admin;

/// <summary>
/// Direct unit tests of the builder's pieces that need no host, no <c>DbContext</c> and no
/// <c>HealthCheckService</c>: <see cref="AdminHealthReportBuilder.ParseVersion"/>, and
/// <see cref="AdminHealthReportBuilder.BuildVerificationAsync"/> with a substitute queue. The rest of
/// <see cref="AdminHealthReportBuilder"/> is covered by <c>AdminHealthEndpointTests</c> through the
/// full <c>WebApplicationFactory</c> pipeline.
/// </summary>
public class AdminHealthReportBuilderTests
{
    [Fact]
    public void ParseVersion_WithSourceRevisionId_SplitsVersionAndShortensShaToSevenChars()
    {
        var (version, buildSha) = AdminHealthReportBuilder.ParseVersion("1.0.0+abcdef1234");

        Assert.Equal("1.0.0", version);
        Assert.Equal("abcdef1", buildSha);
    }

    [Fact]
    public void ParseVersion_WithoutAPlus_ReturnsNullBuildSha()
    {
        var (version, buildSha) = AdminHealthReportBuilder.ParseVersion("1.0.0");

        Assert.Equal("1.0.0", version);
        Assert.Null(buildSha);
    }

    [Fact]
    public void ParseVersion_ShaShorterThanSevenChars_IsReturnedAsIs()
    {
        var (version, buildSha) = AdminHealthReportBuilder.ParseVersion("2.3.1+ab12");

        Assert.Equal("2.3.1", version);
        Assert.Equal("ab12", buildSha);
    }

    [Fact]
    public void ParseVersion_TrailingPlusWithNoRevisionId_ReturnsNullBuildSha()
    {
        var (version, buildSha) = AdminHealthReportBuilder.ParseVersion("1.0.0+");

        Assert.Equal("1.0.0", version);
        Assert.Null(buildSha);
    }

    [Fact]
    public void ParseVersion_NullOrBlank_FallsBackToZeroVersionAndNullSha()
    {
        var (version, buildSha) = AdminHealthReportBuilder.ParseVersion(null);

        Assert.Equal("0.0.0", version);
        Assert.Null(buildSha);
    }

    // ── Verification section (#367) ─────────────────────────────────────────

    private static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A substitute reporting <paramref name="count"/> pending and, by default, an empty
    /// (successfully read, nothing failing) recent-failures list -- stubbed explicitly rather than
    /// left to NSubstitute's default, so tests using this helper get a deterministic, non-null
    /// <c>RecentFailures</c> instead of accidentally exercising the degrade-to-null path.</summary>
    private static IVerificationRequestRepository PendingCount(int count, IReadOnlyList<VerificationFailureGroup>? failureGroups = null)
    {
        var requests = Substitute.For<IVerificationRequestRepository>();
        requests.CountPendingAsync(Arg.Any<CancellationToken>()).Returns(count);
        requests.GetRecentFailureGroupsAsync(Arg.Any<CancellationToken>()).Returns(failureGroups ?? Array.Empty<VerificationFailureGroup>());
        return requests;
    }

    private static VerificationJobHeartbeat FailedCycle()
    {
        var heartbeat = new VerificationJobHeartbeat(new FakeTimeProvider(new DateTimeOffset(T0)));
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunFailed(new InvalidOperationException("verification boom"), T0.AddSeconds(30));
        return heartbeat;
    }

    /// <summary>The builder maps <c>StuckAbandoned</c> and <c>FailuresUnrecorded</c> straight off the
    /// heartbeat snapshot, in that order, after <c>RequestsFailed</c> — swapping them would pass every
    /// other assertion here, which is why this pins the full record rather than the two fields in
    /// isolation.</summary>
    [Fact]
    public async Task BuildVerificationAsync_ReportsTheHeartbeatSnapshot_AndThePendingCount()
    {
        var heartbeat = new VerificationJobHeartbeat(new FakeTimeProvider(new DateTimeOffset(T0)));
        heartbeat.MarkRunStarted();
        heartbeat.MarkRunSucceeded(processed: 5, failed: 2, stuckAbandoned: 4, failuresUnrecorded: 1, nextRunDueUtc: T0.AddSeconds(30));

        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            heartbeat, enabled: true, PendingCount(7), includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        // RecentFailures compared separately below (Assert.Equal on a record can't deep-compare a
        // list field -- two equal-content List<T> instances aren't reference-equal, so the field is
        // fed back from dto itself here to isolate this assertion to the other fields, exactly the
        // way it already pinned RequestsFailed/StuckAbandoned/FailuresUnrecorded ordering).
        Assert.Equal(
            new AdminHealthVerificationDto(true, T0, T0, "Success", 5, 2, 4, 1, T0.AddSeconds(30), null, 7, RecentFailures: dto.RecentFailures),
            dto);
        Assert.NotNull(dto.RecentFailures);
        Assert.Empty(dto.RecentFailures!);
    }

    [Fact]
    public async Task BuildVerificationAsync_Disabled_ReportsNeverRunWithNullTimestamps()
    {
        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            new VerificationJobHeartbeat(TimeProvider.System), enabled: false, PendingCount(0),
            includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(
            new AdminHealthVerificationDto(false, null, null, "NeverRun", 0, 0, 0, 0, null, null, 0, RecentFailures: dto.RecentFailures),
            dto);
        Assert.NotNull(dto.RecentFailures);
        Assert.Empty(dto.RecentFailures!);
    }

    [Fact]
    public async Task BuildVerificationAsync_OutsideProduction_IncludesTheLastError()
    {
        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            FailedCycle(), enabled: true, PendingCount(3), includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("Failed", dto.LastOutcome);
        Assert.Equal("InvalidOperationException: verification boom", dto.LastError);
    }

    [Fact]
    public async Task BuildVerificationAsync_InProduction_NullsTheLastError_ButStillReportsTheFailedOutcome()
    {
        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            FailedCycle(), enabled: true, PendingCount(3), includeDiagnostics: false, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("Failed", dto.LastOutcome);
        Assert.Null(dto.LastError);
        Assert.Equal(3, dto.PendingCount);
    }

    /// <summary>#401 task 2, R4/R5: a paused cycle's outcome and its RequestsPaused count both reach
    /// the DTO, distinct from a Success with zero paused.</summary>
    [Fact]
    public async Task BuildVerificationAsync_APausedCycle_ReportsThePausedOutcomeAndCount()
    {
        var heartbeat = new VerificationJobHeartbeat(new FakeTimeProvider(new DateTimeOffset(T0)));
        heartbeat.MarkRunPaused(processed: 6, failed: 1, stuckAbandoned: 0, failuresUnrecorded: 0, paused: 4, nextRunDueUtc: T0.AddSeconds(30));

        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            heartbeat, enabled: true, PendingCount(2), includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(
            new AdminHealthVerificationDto(true, null, T0, "Paused", 6, 1, 0, 0, T0.AddSeconds(30), null, 2, RequestsPaused: 4, RecentFailures: dto.RecentFailures),
            dto);
        Assert.NotNull(dto.RecentFailures);
        Assert.Empty(dto.RecentFailures!);
    }

    [Fact]
    public async Task BuildVerificationAsync_CountingThrows_ReportsANullPendingCount_WithoutFailingTheSection()
    {
        var requests = Substitute.For<IVerificationRequestRepository>();
        requests.CountPendingAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<int>(new InvalidOperationException("database unreachable")));
        requests.GetRecentFailureGroupsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<VerificationFailureGroup>());

        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            FailedCycle(), enabled: true, requests, includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        Assert.Null(dto.PendingCount);
        Assert.Equal("Failed", dto.LastOutcome);
        Assert.Equal("InvalidOperationException: verification boom", dto.LastError);
    }

    // ── Recent failures (#424) ──────────────────────────────────────────────

    /// <summary>R3: a null <c>FailedSource</c> group is mapped through as its own "unattributed"
    /// entry, not dropped -- alongside a normal, source-attributed group, so the mapping is proven to
    /// carry every group the repository returns rather than only the ones with a source.</summary>
    [Fact]
    public async Task BuildVerificationAsync_RecentFailuresRead_MapsEveryGroup_IncludingTheUnattributedOne()
    {
        var groups = new List<VerificationFailureGroup>
        {
            new("aeroapi", VerificationEntityType.Airport, 3),
            new(null, VerificationEntityType.Airline, 1),
        };
        var requests = Substitute.For<IVerificationRequestRepository>();
        requests.CountPendingAsync(Arg.Any<CancellationToken>()).Returns(0);
        requests.GetRecentFailureGroupsAsync(Arg.Any<CancellationToken>()).Returns(groups);

        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            new VerificationJobHeartbeat(TimeProvider.System), enabled: true, requests,
            includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                new AdminHealthVerificationFailureDto("aeroapi", "Airport", 3),
                new AdminHealthVerificationFailureDto(null, "Airline", 1),
            },
            dto.RecentFailures);
    }

    /// <summary>R7, paired with the three tests above that already prove the success-with-nothing-failing
    /// shape (a non-null, empty list): a throwing read degrades to <c>null</c> ("cannot tell") instead,
    /// so the two outcomes are provably distinct rather than both collapsing to "nothing is wrong".</summary>
    [Fact]
    public async Task BuildVerificationAsync_RecentFailuresReadThrows_ReportsNull_DistinctFromAnEmptyList()
    {
        var requests = Substitute.For<IVerificationRequestRepository>();
        requests.CountPendingAsync(Arg.Any<CancellationToken>()).Returns(0);
        requests.GetRecentFailureGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<VerificationFailureGroup>>(new InvalidOperationException("database unreachable")));

        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            new VerificationJobHeartbeat(TimeProvider.System), enabled: true, requests,
            includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        Assert.Null(dto.RecentFailures);
    }
}
