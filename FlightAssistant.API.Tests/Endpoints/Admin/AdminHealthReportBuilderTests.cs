using FlightAssistant.API.Endpoints.Admin;
using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Contracts.Responses.Admin;
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

    private static IVerificationRequestRepository PendingCount(int count)
    {
        var requests = Substitute.For<IVerificationRequestRepository>();
        requests.CountPendingAsync(Arg.Any<CancellationToken>()).Returns(count);
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

        Assert.Equal(new AdminHealthVerificationDto(true, T0, T0, "Success", 5, 2, 4, 1, T0.AddSeconds(30), null, 7), dto);
    }

    [Fact]
    public async Task BuildVerificationAsync_Disabled_ReportsNeverRunWithNullTimestamps()
    {
        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            new VerificationJobHeartbeat(TimeProvider.System), enabled: false, PendingCount(0),
            includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(new AdminHealthVerificationDto(false, null, null, "NeverRun", 0, 0, 0, 0, null, null, 0), dto);
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

    [Fact]
    public async Task BuildVerificationAsync_CountingThrows_ReportsANullPendingCount_WithoutFailingTheSection()
    {
        var requests = Substitute.For<IVerificationRequestRepository>();
        requests.CountPendingAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<int>(new InvalidOperationException("database unreachable")));

        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            FailedCycle(), enabled: true, requests, includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        Assert.Null(dto.PendingCount);
        Assert.Equal("Failed", dto.LastOutcome);
        Assert.Equal("InvalidOperationException: verification boom", dto.LastError);
    }
}
