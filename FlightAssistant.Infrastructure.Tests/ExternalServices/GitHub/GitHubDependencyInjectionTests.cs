using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using FlightAssistant.Application;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Admin.Ci;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.ExternalServices.GitHub;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.GitHub;

/// <summary>
/// Builds the real container. Every provider test constructs <see cref="GitHubCiRunProvider"/> with
/// hand-made clients, so only these tests can see what DI actually hands it: which client carries
/// the token, and that neither follows a redirect.
/// </summary>
public class GitHubDependencyInjectionTests
{
    private static ServiceProvider BuildContainer(
        string? token = "github_pat_test_token", Action<IServiceCollection>? configure = null)
    {
        var settings = new Dictionary<string, string?> { ["AeroApi:ApiKey"] = "dummy-test-key" };
        if (token is not null)
            settings["GitHub:Token"] = token;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddAppCaching(config);
        services.AddInfrastructure(config, isDevelopment: true);
        services.AddApplication();
        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, AuthenticationHeaderValue? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization));
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>
    /// The provider tests hand the provider its two clients themselves, so they can't see a
    /// registration that passes the token client as the download client too. That swap would send
    /// the token to blob storage. Here the provider comes from the container, and only the transports
    /// underneath its named clients are replaced.
    /// </summary>
    [Fact]
    public async Task TheProviderFromTheContainer_DownloadsTheArchiveWithTheCredentiallessClient()
    {
        const string blobUrl = "https://productionresultssa6.blob.core.windows.net/actions-results/abc/test-results.zip?sig=signed";

        var api = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(blobUrl) } });

        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("test-results.json").Open());
            writer.Write(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GitHub", "test-results.json")));
        }

        var download = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive.ToArray()) });

        using var provider = BuildContainer(configure: services =>
        {
            services.AddHttpClient(GitHubCiRunProvider.ApiClientName).ConfigurePrimaryHttpMessageHandler(() => api);
            services.AddHttpClient(GitHubCiRunProvider.DownloadClientName).ConfigurePrimaryHttpMessageHandler(() => download);
        });
        using var scope = provider.CreateScope();

        var report = await scope.ServiceProvider.GetRequiredService<ICiRunProvider>().GetTestReportAsync(10319006519);

        Assert.Equal(8, report.Total);
        Assert.Equal("github_pat_test_token", Assert.Single(api.Requests).Authorization?.Parameter);
        var (uri, authorization) = Assert.Single(download.Requests);
        Assert.Equal(new Uri(blobUrl), uri);
        Assert.Null(authorization);
    }

    [Fact]
    public void AddInfrastructure_ResolvesICiRunProvider_AsTheGitHubProvider()
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        Assert.IsType<GitHubCiRunProvider>(scope.ServiceProvider.GetRequiredService<ICiRunProvider>());
    }

    [Fact]
    public void AddApplication_ResolvesBothCiQueryHandlers()
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRequestHandler<GetCiRunsQuery, IReadOnlyList<CiRun>>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRequestHandler<GetCiRunTestsQuery, CiTestReport>>());
    }

    [Fact]
    public void GitHubOptions_DefaultToThisRepositorysCiWorkflow()
    {
        using var provider = BuildContainer();

        var options = provider.GetRequiredService<IOptions<GitHubOptions>>().Value;

        Assert.Equal("salih1f1", options.Owner);
        Assert.Equal("flight-assistant-backend", options.Repo);
        Assert.Equal("ci.yml", options.Workflow);
    }

    [Fact]
    public void TheApiClient_CarriesTheTokenAndGitHubsHeaders()
    {
        using var provider = BuildContainer();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubCiRunProvider.ApiClientName);

        Assert.Equal(new Uri("https://api.github.com/"), client.BaseAddress);
        Assert.Equal("Bearer", client.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("github_pat_test_token", client.DefaultRequestHeaders.Authorization?.Parameter);
        Assert.Contains(client.DefaultRequestHeaders.Accept, a => a.MediaType == "application/vnd.github+json");
        Assert.Equal("2022-11-28", client.DefaultRequestHeaders.GetValues("X-GitHub-Api-Version").Single());
        Assert.NotEmpty(client.DefaultRequestHeaders.UserAgent);
    }

    [Fact]
    public void WithoutAToken_TheOptionsStillValidate_AndTheApiClientSendsNoAuthorization()
    {
        using var provider = BuildContainer(token: null);

        // .Value runs the data-annotation validation ValidateOnStart runs at startup.
        var options = provider.GetRequiredService<IOptions<GitHubOptions>>().Value;
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubCiRunProvider.ApiClientName);

        Assert.False(options.IsConfigured);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void TheDownloadClient_CarriesNoCredentials()
    {
        using var provider = BuildContainer();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubCiRunProvider.DownloadClientName);

        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Null(client.BaseAddress);
    }

    /// <summary>
    /// A client that followed redirects itself would send the artifact download on to blob storage
    /// before the provider ever saw the Location, and whether the token went with it would be up to
    /// the handler's redirect rules rather than to this code.
    /// </summary>
    [Theory]
    [InlineData(GitHubCiRunProvider.ApiClientName)]
    [InlineData(GitHubCiRunProvider.DownloadClientName)]
    public void NeitherClient_FollowsRedirects(string clientName)
    {
        using var provider = BuildContainer();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
        while (handler is DelegatingHandler delegating)
            handler = delegating.InnerHandler!;

        var primary = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
    }
}
