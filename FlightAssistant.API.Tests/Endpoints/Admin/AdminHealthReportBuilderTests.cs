using FlightAssistant.API.Endpoints.Admin;
using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.API.Tests.Endpoints.Admin;

/// <summary>
/// Direct unit tests of the builder's pieces that need no host and no <c>HealthCheckService</c>:
/// <see cref="AdminHealthReportBuilder.ParseVersion"/>,
/// <see cref="AdminHealthReportBuilder.BuildVerificationAsync"/> with a substitute queue, and
/// <see cref="AdminHealthReportBuilder.BuildMigrationsAsync"/> against a relational <c>DbContext</c>
/// whose server does not exist — no running Postgres and no Docker, since both of its failure paths
/// are reached before anything is successfully read. The rest of
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

    /// <summary>Deliberately not <c>VerificationOptions</c>'s own default of 180 (#447): a builder that
    /// hardcoded the default, or read it from anywhere but its caller, would still satisfy every
    /// assertion below if this matched it.</summary>
    private const int StaleAfterDays = 45;

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
            heartbeat, enabled: true, StaleAfterDays, PendingCount(7), includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        // RecentFailures compared separately below (Assert.Equal on a record can't deep-compare a
        // list field -- two equal-content List<T> instances aren't reference-equal, so the field is
        // fed back from dto itself here to isolate this assertion to the other fields, exactly the
        // way it already pinned RequestsFailed/StuckAbandoned/FailuresUnrecorded ordering).
        Assert.Equal(
            new AdminHealthVerificationDto(true, T0, T0, "Success", 5, 2, 4, 1, T0.AddSeconds(30), null, 7, StaleAfterDays, RecentFailures: dto.RecentFailures),
            dto);
        Assert.NotNull(dto.RecentFailures);
        Assert.Empty(dto.RecentFailures!);
    }

    [Fact]
    public async Task BuildVerificationAsync_Disabled_ReportsNeverRunWithNullTimestamps()
    {
        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            new VerificationJobHeartbeat(TimeProvider.System), enabled: false, StaleAfterDays, PendingCount(0),
            includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(
            new AdminHealthVerificationDto(false, null, null, "NeverRun", 0, 0, 0, 0, null, null, 0, StaleAfterDays, RecentFailures: dto.RecentFailures),
            dto);
        Assert.NotNull(dto.RecentFailures);
        Assert.Empty(dto.RecentFailures!);
    }

    [Fact]
    public async Task BuildVerificationAsync_OutsideProduction_IncludesTheLastError()
    {
        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            FailedCycle(), enabled: true, StaleAfterDays, PendingCount(3), includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("Failed", dto.LastOutcome);
        Assert.Equal("InvalidOperationException: verification boom", dto.LastError);
    }

    [Fact]
    public async Task BuildVerificationAsync_InProduction_NullsTheLastError_ButStillReportsTheFailedOutcome()
    {
        var dto = await AdminHealthReportBuilder.BuildVerificationAsync(
            FailedCycle(), enabled: true, StaleAfterDays, PendingCount(3), includeDiagnostics: false, NullLogger.Instance, CancellationToken.None);

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
            heartbeat, enabled: true, StaleAfterDays, PendingCount(2), includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(
            new AdminHealthVerificationDto(true, null, T0, "Paused", 6, 1, 0, 0, T0.AddSeconds(30), null, 2, StaleAfterDays, RequestsPaused: 4, RecentFailures: dto.RecentFailures),
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
            FailedCycle(), enabled: true, StaleAfterDays, requests, includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

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
            new VerificationJobHeartbeat(TimeProvider.System), enabled: true, StaleAfterDays, requests,
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
            new VerificationJobHeartbeat(TimeProvider.System), enabled: true, StaleAfterDays, requests,
            includeDiagnostics: true, NullLogger.Instance, CancellationToken.None);

        Assert.Null(dto.RecentFailures);
    }

    // ── An aborted request (#453) ───────────────────────────────────────────

    /// <summary>
    /// A relational context whose server does not exist. <c>IsRelational()</c> answers a *provider*
    /// question — true for Npgsql whether or not Postgres is reachable (CLAUDE.md, "Cannot tell is
    /// not nothing pending") — which is exactly what these two tests need: the guard clause is
    /// passed and the migration-history reads below are genuinely reached, with no server, no
    /// container and no Docker. Port 1 is refused immediately rather than hanging, and the two
    /// one-second timeouts are a backstop for a host where it is not.
    /// </summary>
    private static FlightAssistantDbContext RelationalContextPointedAtNothing() =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1;Command Timeout=1")
            .Options);

    /// <summary>
    /// <c>/admin/health</c> runs every health check with no predicate, the external <c>aeroapi</c>
    /// and <c>apns</c> ones included, so it is the likeliest route in the API to be abandoned
    /// mid-flight — a panel refresh, a navigation, a fetch timeout. When that happens the request's
    /// own token is cancelled and the migration-history read throws; catching it would log a
    /// migration failure that never happened and carry on assembling a report for a connection that
    /// is already gone. The caller left, so this is not "cannot tell": it is nothing to tell anyone.
    /// </summary>
    [Fact]
    public async Task BuildMigrationsAsync_TheRequestWasAborted_LetsItThrow_AndLogsNoMigrationFailure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var logger = new CapturingLogger();
        await using var db = RelationalContextPointedAtNothing();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AdminHealthReportBuilder.BuildMigrationsAsync(db, includeDiagnostics: true, logger, cts.Token));

        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// The other side of the filter, and the behaviour it must not cost us (final review, C1): with
    /// the caller still there, a database that cannot be reached degrades to <c>Available: false</c>
    /// — "cannot tell", never an empty <c>Pending</c> list, which is a different and much stronger
    /// claim — and does log, instead of taking the whole report down the way an unguarded
    /// <c>NpgsqlException</c> did.
    /// </summary>
    [Fact]
    public async Task BuildMigrationsAsync_TheDatabaseIsUnreachable_ReportsCannotTell_AndLogsAWarning()
    {
        var logger = new CapturingLogger();
        await using var db = RelationalContextPointedAtNothing();

        var dto = await AdminHealthReportBuilder.BuildMigrationsAsync(
            db, includeDiagnostics: true, logger, CancellationToken.None);

        Assert.False(dto.Available);
        Assert.Empty(dto.Pending);
        Assert.NotNull(dto.Error);
        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal("Failed to read EF Core migration state for /admin/health.", message);
    }

    /// <summary>
    /// The same rule on the section next door, which has had this filter since #367 but has never
    /// been pinned by a test: an abandoned request must not be reported as <c>PendingCount: null</c>.
    /// A client hang-up and a database that cannot answer are different things, and only the second
    /// one is worth an operator's attention — the pair of them is the whole point of the filter.
    /// </summary>
    [Fact]
    public async Task BuildVerificationAsync_TheRequestWasAborted_LetsItThrow_RatherThanReportingCannotTell()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var requests = Substitute.For<IVerificationRequestRepository>();
        requests.CountPendingAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<int>(new OperationCanceledException()));
        var logger = new CapturingLogger();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AdminHealthReportBuilder.BuildVerificationAsync(
                new VerificationJobHeartbeat(TimeProvider.System), enabled: true, StaleAfterDays, requests,
                includeDiagnostics: true, logger, cts.Token));

        Assert.Empty(logger.Entries);
    }

    /// <summary>Records every log call so a test can assert one was — or was not — emitted.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
