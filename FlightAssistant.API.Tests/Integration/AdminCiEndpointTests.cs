using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Contracts.Responses.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>/admin/ci</c> hangs off the shared <c>/admin</c> group, so the auth cases check it was mapped
/// there. The provider is substituted everywhere except the one test proving that, with no
/// GitHub:Token (as in Testing config), the real provider answers 503 without calling GitHub.
/// </summary>
[Collection("Integration")]
public class AdminCiEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private static readonly CiRun Run = new(
        Id: 34761036906,
        RunNumber: 32,
        RunAttempt: 1,
        Title: "Merge pull request #330 from salih1f1/fix/align-admin-panel-dev-origin",
        Event: "push",
        Branch: "development",
        CommitSha: "1cc8f1a2966782782a2456e85d91eaa2fe0ff192",
        Status: "completed",
        Conclusion: "success",
        CreatedUtc: new DateTimeOffset(2026, 9, 13, 13, 52, 9, TimeSpan.Zero),
        StartedUtc: new DateTimeOffset(2026, 9, 13, 13, 52, 9, TimeSpan.Zero),
        UpdatedUtc: new DateTimeOffset(2026, 9, 13, 13, 54, 29, TimeSpan.Zero),
        HtmlUrl: "https://github.com/salih1f1/flight-assistant-backend/actions/runs/34761036906");

    private static readonly CiTestReport Report = new(
        "1cc8f1a2966782782a2456e85d91eaa2fe0ff192", "development", 2, 1, 1, 0, 14,
        [
            new CiTestSuite("FlightAssistant.Application.Tests", 2, 1, 1, 0, 14,
            [
                new CiTestCase("MembershipNotFound_Throws", "UpdateLoyaltyMembershipCommandHandlerTests", "Passed", 2, ""),
                new CiTestCase("Handle_NewAirport_InsertsAndReturnsInsertedCount", "ImportAirportsCommandHandlerTests", "Failed", 12,
                    "Assert.Equal() Failure: Values differ"),
            ]),
        ]);

    private static ICiRunProvider StubProvider()
    {
        var provider = Substitute.For<ICiRunProvider>();
        provider.GetRunsAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<CiRun> { Run });
        provider.FindTestResultsArtifactAsync(Run.Id, Arg.Any<CancellationToken>())
            .Returns(new CiTestResultsArtifact(10319006519, false, Run.UpdatedUtc));
        provider.GetTestReportAsync(10319006519, Arg.Any<CancellationToken>())
            .Returns(Report);
        return provider;
    }

    private WebApplicationFactory<Program> WithProvider(ICiRunProvider provider) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddScoped(_ => provider)));

    /// <summary>WithWebHostBuilder hands back the base factory type, so CreateAdminClient is out of
    /// reach; same token shape, built here.</summary>
    private static HttpClient AdminClient(WebApplicationFactory<Program> scopedFactory)
    {
        var client = scopedFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", $"test-admin-{Guid.NewGuid()}");
        return client;
    }

    [Theory]
    [InlineData("admin/ci/runs")]
    [InlineData("admin/ci/runs/34761036906/tests")]
    public async Task Anonymous_Returns401(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("admin/ci/runs")]
    [InlineData("admin/ci/runs/34761036906/tests")]
    public async Task AuthenticatedWithoutTheAdminRole_Returns403(string path)
    {
        var response = await factory.CreateUserClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetCiRuns_Admin_Returns200_WithTheRunsAndTheirDuration()
    {
        var provider = StubProvider();
        using var scopedFactory = WithProvider(provider);

        var response = await AdminClient(scopedFactory).GetAsync("admin/ci/runs?branch=development");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = Assert.Single((await response.Content.ReadFromJsonAsync<List<CiRunDto>>())!);
        Assert.Equal(Run.Id, run.Id);
        Assert.Equal("development", run.Branch);
        Assert.Equal("success", run.Conclusion);
        Assert.Equal(140, run.DurationSeconds);
        Assert.Equal(Run.HtmlUrl, run.HtmlUrl);
        await provider.Received(1).GetRunsAsync("development", 20, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("51")]
    public async Task GetCiRuns_LimitOutOfRange_Returns400(string limit)
    {
        using var scopedFactory = WithProvider(StubProvider());

        var response = await AdminClient(scopedFactory).GetAsync($"admin/ci/runs?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetCiRunTests_Admin_Returns200_WithTheReport()
    {
        using var scopedFactory = WithProvider(StubProvider());

        var response = await AdminClient(scopedFactory).GetAsync($"admin/ci/runs/{Run.Id}/tests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<CiTestReportDto>())!;
        Assert.Equal(Run.Id, report.RunId);
        Assert.Equal((2, 1, 1), (report.Total, report.Passed, report.Failed));
        var failed = Assert.Single(Assert.Single(report.Suites).Tests, t => t.Outcome == "Failed");
        Assert.Equal("ImportAirportsCommandHandlerTests", failed.Class);
        Assert.Equal("Assert.Equal() Failure: Values differ", failed.Message);
    }

    [Fact]
    public async Task GetCiRunTests_ARunWithNoReport_Returns404_SayingWhy()
    {
        var provider = StubProvider();
        provider.FindTestResultsArtifactAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns((CiTestResultsArtifact?)null);
        using var scopedFactory = WithProvider(provider);

        var response = await AdminClient(scopedFactory).GetAsync("admin/ci/runs/34760947297/tests");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("has no test report", problem!.Title);
    }

    [Fact]
    public async Task GetCiRunTests_ANonNumericId_Returns404()
    {
        using var scopedFactory = WithProvider(StubProvider());

        var response = await AdminClient(scopedFactory).GetAsync("admin/ci/runs/latest/tests");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WithoutAGitHubToken_TheRealProvider_Returns503_SayingCiIsNotConfigured()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/ci/runs");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("CI results aren't configured on this API.", problem!.Title);
    }
}
