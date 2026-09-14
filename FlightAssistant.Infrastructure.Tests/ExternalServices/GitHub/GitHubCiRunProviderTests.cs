using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.GitHub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.GitHub;

/// <summary>
/// Drives <see cref="GitHubCiRunProvider"/> against responses recorded from this repository's CI (see
/// <c>Fixtures/GitHub</c>), trimmed to the fields that matter and stripped of actor and commit-author
/// details. Three edits, each so a test can catch what the recording alone can't: the third run is
/// marked in progress; run-artifacts.json lists an older attempt's artifact first; and
/// test-results.json keeps two tests per suite, with one marked failed.
/// </summary>
public class GitHubCiRunProviderTests
{
    private const string Token = "github_pat_test_token";

    private const string SignedDownloadUrl =
        "https://productionresultssa6.blob.core.windows.net/actions-results/abc/test-results.zip?sig=signed";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GitHub", name));

    /// <summary>Answers every request through <paramref name="respond"/> and records what was sent,
    /// including the Authorization header the client attached.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, AuthenticationHeaderValue? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed record Setup(GitHubCiRunProvider Provider, StubHandler Api, StubHandler Download);

    /// <summary>Clients set up the way the DI registration sets them up: the token on the API client,
    /// nothing on the download client.</summary>
    private static Setup Create(
        Func<HttpRequestMessage, HttpResponseMessage> api,
        Func<HttpRequestMessage, HttpResponseMessage>? download = null,
        string? token = Token)
    {
        var apiHandler = new StubHandler(api);
        var downloadHandler = new StubHandler(download ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var apiClient = new HttpClient(apiHandler) { BaseAddress = new Uri(GitHubOptions.ApiBaseUrl) };
        if (token is not null)
            apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var provider = new GitHubCiRunProvider(
            apiClient,
            new HttpClient(downloadHandler),
            Options.Create(new GitHubOptions { Token = token }),
            NullLogger<GitHubCiRunProvider>.Instance);

        return new Setup(provider, apiHandler, downloadHandler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Redirect(string? location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        if (location is not null)
            response.Headers.Location = new Uri(location);
        return response;
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>The real artifact's layout: the JSON report next to a .trx per test project.</summary>
    private static byte[] RecordedArchive() => Zip(
        ("test-results.json", Fixture("test-results.json")),
        ("TestResults/FlightAssistant.API.Tests.trx", "<TestRun />"));

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    // ── Runs ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRunsAsync_ParsesTheRecordedRuns()
    {
        var s = Create(_ => Json(Fixture("workflow-runs.json")));

        var runs = await s.Provider.GetRunsAsync(null, 3);

        Assert.Equal(new[] { 34761036906L, 34760947297L, 34150486189L }, runs.Select(r => r.Id));

        var push = runs[0];
        Assert.Equal(32, push.RunNumber);
        Assert.Equal(1, push.RunAttempt);
        Assert.Equal("Merge pull request #330 from salih1f1/fix/align-admin-panel-dev-origin", push.Title);
        Assert.Equal("push", push.Event);
        Assert.Equal("development", push.Branch);
        Assert.Equal("1cc8f1a2966782782a2456e85d91eaa2fe0ff192", push.CommitSha);
        Assert.Equal("completed", push.Status);
        Assert.Equal("success", push.Conclusion);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 13, 52, 9, TimeSpan.Zero), push.CreatedUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 13, 52, 9, TimeSpan.Zero), push.StartedUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 13, 54, 29, TimeSpan.Zero), push.UpdatedUtc);
        Assert.Equal(140, push.DurationSeconds);
        Assert.Equal("https://github.com/salih1f1/flight-assistant-backend/actions/runs/34761036906", push.HtmlUrl);

        Assert.Equal("pull_request", runs[1].Event);
        Assert.Equal("fix/align-admin-panel-dev-origin", runs[1].Branch);

        Assert.Equal("in_progress", runs[2].Status);
        Assert.Null(runs[2].Conclusion);
        Assert.Null(runs[2].DurationSeconds);
    }

    [Fact]
    public async Task GetRunsAsync_AsksForTheConfiguredWorkflow_WithTheLimitAndAnEscapedBranch()
    {
        var s = Create(_ => Json("""{"total_count":0,"workflow_runs":[]}"""));

        var runs = await s.Provider.GetRunsAsync("feature/332-ci runs", 5);

        Assert.Empty(runs);
        var (uri, _) = Assert.Single(s.Api.Requests);
        Assert.Equal(
            "https://api.github.com/repos/salih1f1/flight-assistant-backend/actions/workflows/ci.yml/runs",
            uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("?per_page=5&branch=feature%2F332-ci%20runs", uri.Query);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task GetRunsAsync_LeavesTheBranchOut_WhenNoneIsGiven(string? branch)
    {
        var s = Create(_ => Json("""{"total_count":0,"workflow_runs":[]}"""));

        await s.Provider.GetRunsAsync(branch, 20);

        Assert.Equal("?per_page=20", Assert.Single(s.Api.Requests).Uri.Query);
    }

    [Fact]
    public async Task GetRunsAsync_TreatsA404_AsOurConfiguration_SinceGitHubHidesPrivateRepositoriesThatWay()
    {
        var s = Create(_ => Json("""{"message":"Not Found","status":"404"}""", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.GetRunsAsync(null, 20));

        Assert.Equal(500, ex.StatusCode);
        Assert.Contains("GitHub:Workflow", ex.DiagnosticDetail);
    }

    public static TheoryData<HttpStatusCode, string?, bool, string, int> UpstreamFailures => new()
    {
        // A token GitHub doesn't accept, or one without Actions: read: our configuration.
        { HttpStatusCode.Unauthorized, null, false, "Bad credentials", 500 },
        { HttpStatusCode.Forbidden, null, false, "Resource not accessible by personal access token", 500 },
        // Primary rate limit: told apart from a permissions 403 by the header alone.
        { HttpStatusCode.Forbidden, "0", false, "Forbidden", 503 },
        // Secondary rate limit: by Retry-After, or by the message.
        { HttpStatusCode.Forbidden, null, true, "Forbidden", 503 },
        { HttpStatusCode.Forbidden, null, false, "You have exceeded a secondary rate limit.", 503 },
        { HttpStatusCode.TooManyRequests, null, false, "Too many requests", 503 },
        { HttpStatusCode.InternalServerError, null, false, "Server Error", 502 },
        { HttpStatusCode.ServiceUnavailable, null, false, "Unavailable", 502 },
    };

    [Theory]
    [MemberData(nameof(UpstreamFailures))]
    public async Task GetRunsAsync_MapsUpstreamFailures(
        HttpStatusCode status, string? rateLimitRemaining, bool retryAfter, string message, int expected)
    {
        var s = Create(_ =>
        {
            var response = Json($$"""{"message":"{{message}}","documentation_url":"https://docs.github.com/rest"}""", status);
            if (rateLimitRemaining is not null)
                response.Headers.Add("x-ratelimit-remaining", rateLimitRemaining);
            if (retryAfter)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        });

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.GetRunsAsync(null, 20));

        Assert.Equal(expected, ex.StatusCode);
    }

    [Fact]
    public async Task GetRunsAsync_Maps200WithAnUnparseableBody_To502()
    {
        var s = Create(_ => Json("<html>not json</html>"));

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.GetRunsAsync(null, 20));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task GetRunsAsync_Maps502_WhenTheRequestFailsBeforeAnyResponse()
    {
        var s = Create(_ => throw new HttpRequestException("connection reset by peer"));

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.GetRunsAsync(null, 20));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task GetRunsAsync_MapsATimeoutTo502_ButLetsTheCallersOwnCancellationThrough()
    {
        var timedOut = Create(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        var ex = await Assert.ThrowsAsync<DomainException>(() => timedOut.Provider.GetRunsAsync(null, 20));
        Assert.Equal(502, ex.StatusCode);

        var s = Create(_ => Json("""{"workflow_runs":[]}"""));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Provider.GetRunsAsync(null, 20, cancelled.Token));
    }

    [Fact]
    public async Task Errors_NeverCarryTheToken()
    {
        var s = Create(_ => Json($$"""{"message":"Bad credentials: {{Token}}"}""", HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.GetRunsAsync(null, 20));

        Assert.DoesNotContain(Token, ex.DiagnosticDetail);
        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains("[REDACTED]", ex.DiagnosticDetail);
    }

    [Fact]
    public async Task EveryCall_WithoutAToken_Is503_AndNeverReachesGitHub()
    {
        var s = Create(_ => Json("{}"), _ => Json("{}"), token: null);
        var calls = new Func<Task>[]
        {
            () => s.Provider.GetRunsAsync(null, 20),
            () => s.Provider.FindTestResultsArtifactAsync(34761036906),
            () => s.Provider.GetTestReportAsync(10319006519),
        };

        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<DomainException>(call);
            Assert.Equal(503, ex.StatusCode);
        }

        Assert.Empty(s.Api.Requests);
        Assert.Empty(s.Download.Requests);
    }

    // ── Artifacts ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindTestResultsArtifactAsync_PicksTheNewestTestResultsArtifact()
    {
        var s = Create(_ => Json(Fixture("run-artifacts.json")));

        var artifact = await s.Provider.FindTestResultsArtifactAsync(34761036906);

        Assert.NotNull(artifact);
        Assert.Equal(10319006519, artifact!.Id);
        Assert.False(artifact.Expired);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 13, 54, 19, TimeSpan.Zero), artifact.CreatedUtc);

        var (uri, _) = Assert.Single(s.Api.Requests);
        Assert.Equal("/repos/salih1f1/flight-assistant-backend/actions/runs/34761036906/artifacts", uri.AbsolutePath);
        Assert.Contains("name=test-results", uri.Query);
    }

    [Fact]
    public async Task FindTestResultsArtifactAsync_ReadsTheExpiredFlag()
    {
        var s = Create(_ => Json(
            """{"total_count":1,"artifacts":[{"id":9,"name":"test-results","expired":true,"created_at":"2026-08-01T10:00:00Z"}]}"""));

        var artifact = await s.Provider.FindTestResultsArtifactAsync(1);

        Assert.True(artifact!.Expired);
    }

    [Fact]
    public async Task FindTestResultsArtifactAsync_ReturnsNull_WhenTheRunHasNoTestResultsArtifact()
    {
        // Named here too, even though the request filters by name, so another artifact's archive is
        // never read as a test report.
        var s = Create(_ => Json(
            """{"total_count":1,"artifacts":[{"id":9,"name":"openapi-spec","expired":false,"created_at":"2026-09-13T13:54:19Z"}]}"""));

        Assert.Null(await s.Provider.FindTestResultsArtifactAsync(34761036906));
    }

    [Fact]
    public async Task FindTestResultsArtifactAsync_UnknownRun_Is404()
    {
        var s = Create(_ => Json("""{"message":"Not Found","status":"404"}""", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.FindTestResultsArtifactAsync(1));

        Assert.Equal(404, ex.StatusCode);
    }

    // ── Report ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTestReportAsync_FollowsTheRedirect_WithoutSendingTheToken()
    {
        var s = Create(_ => Redirect(SignedDownloadUrl), _ => Bytes(RecordedArchive()));

        await s.Provider.GetTestReportAsync(10319006519);

        var (apiUri, apiAuthorization) = Assert.Single(s.Api.Requests);
        Assert.Equal("/repos/salih1f1/flight-assistant-backend/actions/artifacts/10319006519/zip", apiUri.AbsolutePath);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", Token), apiAuthorization);

        var (downloadUri, downloadAuthorization) = Assert.Single(s.Download.Requests);
        Assert.Equal(new Uri(SignedDownloadUrl), downloadUri);
        Assert.Null(downloadAuthorization);
    }

    [Fact]
    public async Task GetTestReportAsync_ParsesTheReport_TotalsSuitesAndFailures()
    {
        var s = Create(_ => Redirect(SignedDownloadUrl), _ => Bytes(RecordedArchive()));

        var report = await s.Provider.GetTestReportAsync(10319006519);

        Assert.Equal("1cc8f1a2966782782a2456e85d91eaa2fe0ff192", report.Commit);
        Assert.Equal("development", report.Ref);
        Assert.Equal((8, 7, 1, 0, 275L), (report.Total, report.Passed, report.Failed, report.Skipped, report.DurationMs));
        Assert.Equal(
            new[] { "FlightAssistant.API.Tests", "FlightAssistant.Application.Tests", "FlightAssistant.Domain.Tests", "FlightAssistant.Infrastructure.Tests" },
            report.Suites.Select(suite => suite.Suite));

        var application = report.Suites[1];
        Assert.Equal((2, 1, 1, 0, 14L), (application.Total, application.Passed, application.Failed, application.Skipped, application.DurationMs));

        var failed = application.Tests[1];
        Assert.Equal(
            "FlightAssistant.Application.Tests.Commands.Admin.Import.ImportAirportsCommandHandlerTests.Handle_NewAirport_InsertsAndReturnsInsertedCount",
            failed.Name);
        Assert.Equal("ImportAirportsCommandHandlerTests", failed.Class);
        Assert.Equal("Failed", failed.Outcome);
        Assert.Equal(12, failed.DurationMs);
        Assert.StartsWith("Assert.Equal() Failure: Values differ\nExpected: 404", failed.Message);

        Assert.Equal("", application.Tests[0].Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task GetTestReportAsync_AnArtifactGitHubNoLongerHas_Is404(HttpStatusCode status)
    {
        var s = Create(_ => Json("""{"message":"Artifact has expired"}""", status), _ => Bytes(RecordedArchive()));

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.GetTestReportAsync(1));

        Assert.Equal(404, ex.StatusCode);
        Assert.Empty(s.Download.Requests);
    }

    [Theory]
    [InlineData("http://productionresultssa6.blob.core.windows.net/actions-results/abc/test-results.zip")]
    [InlineData(null)]
    public async Task GetTestReportAsync_RefusesARedirectThatIsNotToAnHttpsUrl(string? location)
    {
        var s = Create(_ => Redirect(location), _ => Bytes(RecordedArchive()));

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.GetTestReportAsync(1));

        Assert.Equal(502, ex.StatusCode);
        Assert.Empty(s.Download.Requests);
    }

    [Fact]
    public async Task GetTestReportAsync_RefusesASuccessThatIsNotARedirect()
    {
        var s = Create(_ => Bytes(RecordedArchive()), _ => Bytes(RecordedArchive()));

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.GetTestReportAsync(1));

        Assert.Equal(502, ex.StatusCode);
        Assert.Empty(s.Download.Requests);
    }

    [Fact]
    public async Task GetTestReportAsync_AFailedDownload_Is502()
    {
        // What blob storage answers once the signed URL has expired.
        var s = Create(_ => Redirect(SignedDownloadUrl), _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.GetTestReportAsync(1));

        Assert.Equal(502, ex.StatusCode);
    }

    public static TheoryData<string, byte[]> UnreadableArchives => new()
    {
        { "an archive without test-results.json", Zip(("TestResults/FlightAssistant.API.Tests.trx", "<TestRun />")) },
        { "something that is not a zip", Encoding.UTF8.GetBytes("PK? no, just text") },
        { "a report that is not JSON", Zip(("test-results.json", "Traceback (most recent call last):")) },
    };

    [Theory]
    [MemberData(nameof(UnreadableArchives))]
    public async Task GetTestReportAsync_AnUnreadableArchive_Is502(string because, byte[] archive)
    {
        var s = Create(_ => Redirect(SignedDownloadUrl), _ => Bytes(archive));

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.GetTestReportAsync(1));

        Assert.True(ex.StatusCode == 502, because);
    }

    [Fact]
    public async Task GetTestReportAsync_RefusesAnArchiveDeclaredOverTheCap_WithoutReadingIt()
    {
        var s = Create(_ => Redirect(SignedDownloadUrl), _ =>
        {
            var response = Bytes(RecordedArchive());
            response.Content.Headers.ContentLength = GitHubCiRunProvider.MaxArchiveBytes + 1;
            return response;
        });

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.GetTestReportAsync(1));

        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task GetTestReportAsync_RefusesAReportOverTheCap()
    {
        // Compresses to almost nothing, which is exactly why the decompressed size is capped too.
        var oversized = new string(' ', (int)GitHubCiRunProvider.MaxReportBytes + 1);
        var s = Create(_ => Redirect(SignedDownloadUrl), _ => Bytes(Zip(("test-results.json", oversized))));

        var ex = await Assert.ThrowsAsync<DomainException>(() => s.Provider.GetTestReportAsync(1));

        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("limit", ex.DiagnosticDetail);
    }
}
