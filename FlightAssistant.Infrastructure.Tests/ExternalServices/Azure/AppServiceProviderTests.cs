using System.Net;
using System.Text;
using Azure.Core;
using FlightAssistant.Application.Hosting;
using FlightAssistant.Infrastructure.ExternalServices.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Azure;

/// <summary>
/// Drives <see cref="AppServiceProvider"/> against a stub <see cref="HttpMessageHandler"/> --
/// following the pattern in <c>AzureCostProviderTests</c> -- so every state the App Service health
/// section (#530) can show is pinned without ever calling Azure.
/// </summary>
public class AppServiceProviderTests
{
    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond, List<HttpRequestMessage> calls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            calls.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>Always succeeds instantly with a fixed token. Token ACQUISITION is Azure.Identity's
    /// own concern, not this provider's -- these tests exercise only what
    /// <see cref="AppServiceProvider"/> does with the HTTP response once it has one, so the
    /// credential itself is never real and never reaches the network.</summary>
    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("stub-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

    private const string ResourceGroup = "flight-assistant-dev";
    private const string SubscriptionId = "a39b9c33-675b-40ca-84fc-6e548ef6d6c5";

    private const string SiteName = "flight-assistant-dev-api";

    /// <summary>Defaults to a fully-configured App Service (site name, resource group and
    /// subscription id all present) so every test but the "not configured" ones can ignore
    /// configuration entirely. All three are nullable because the provider requires all three, and
    /// each has to be shown to be load-bearing on its own -- a guard that only one test reaches can
    /// lose the other two conditions without any test noticing.</summary>
    private static (AppServiceProvider Provider, List<HttpRequestMessage> Calls) Build(
        string? siteName = SiteName,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        string? resourceGroup = ResourceGroup,
        string? ownerName = SubscriptionId + "+" + ResourceGroup + "-WestEuropewebspace")
    {
        var calls = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(respond ?? (_ => Json("{}")), calls);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://management.azure.com/") };

        var settings = new Dictionary<string, string?>();

        // Omitted rather than set to null: IConfiguration treats an absent key the same way a real
        // developer machine leaves it -- never present at all.
        if (siteName is not null)
            settings["WEBSITE_SITE_NAME"] = siteName;
        if (resourceGroup is not null)
            settings["WEBSITE_RESOURCE_GROUP"] = resourceGroup;
        if (ownerName is not null)
            settings["WEBSITE_OWNER_NAME"] = ownerName;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var provider = new AppServiceProvider(
            http,
            configuration,
            new StubTokenCredential(),
            NullLogger<AppServiceProvider>.Instance);

        return (provider, calls);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // The real response, captured from the live resource with a raw ARM GET. siteConfig IS returned
    // inline by the site GET — no separate /config/web call is needed.
    private static string RealSiteResponse(bool alwaysOn = false) => $$"""
        { "id": "/subscriptions/x/resourceGroups/flight-assistant-dev/providers/Microsoft.Web/sites/flight-assistant-dev-api",
          "name": "flight-assistant-dev-api",
          "properties": {
            "state": "Running",
            "availabilityState": "Normal",
            "enabled": true,
            "siteConfig": { "alwaysOn": {{(alwaysOn ? "true" : "false")}}, "numberOfWorkers": 1 } } }
        """;

    [Fact]
    public async Task NotOnAppService_IsNotConfigured_AndMakesNoCall()
    {
        // No WEBSITE_SITE_NAME: a developer machine. Reaching out anyway would log a confusing
        // failure for a state that is entirely expected.
        var (provider, calls) = Build(siteName: null, respond: _ => Json("{}"));

        var snapshot = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AppServiceState.NotConfigured, snapshot.State);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task TheSiteIsRead_WithItsStateAndAlwaysOn()
    {
        var (provider, _) = Build(respond: _ => Json(RealSiteResponse(alwaysOn: false)));

        var snapshot = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AppServiceState.Ok, snapshot.State);
        Assert.Equal("Running", snapshot.SiteState);
        Assert.Equal("Normal", snapshot.AvailabilityState);
        Assert.True(snapshot.Enabled);
        Assert.False(snapshot.AlwaysOn);
        Assert.Equal(1, snapshot.InstanceCount);
    }

    // The constraint the whole feature rests on: a deliberate hosting choice is not a fault.
    [Fact]
    public async Task AlwaysOnFalse_IsStillOk_NotAFailure()
    {
        var (provider, _) = Build(respond: _ => Json(RealSiteResponse(alwaysOn: false)));

        var snapshot = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AppServiceState.Ok, snapshot.State);
    }

    [Fact]
    public async Task AlwaysOnTrue_IsRead_AsTrue()
    {
        var (provider, _) = Build(respond: _ => Json(RealSiteResponse(alwaysOn: true)));

        Assert.True((await provider.GetStatusAsync(CancellationToken.None)).AlwaysOn);
    }

    // Unknown is not false. The panel decides differently on each, so they must not collapse.
    [Fact]
    public async Task AResponseWithNoSiteConfig_LeavesAlwaysOnNull()
    {
        var (provider, _) = Build(respond: _ => Json("""
            { "name": "x", "properties": { "state": "Running", "enabled": true } }
            """));

        var snapshot = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AppServiceState.Ok, snapshot.State);
        Assert.Null(snapshot.AlwaysOn);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusal_IsNotAuthorized_AndNamesTheRole(HttpStatusCode status)
    {
        var (provider, _) = Build(respond: _ => new HttpResponseMessage(status));

        var snapshot = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AppServiceState.NotAuthorized, snapshot.State);
        Assert.Contains("Reader", snapshot.Message);
    }

    // A refusal must not look like a site that is switched off, nor like Always On being absent.
    [Fact]
    public async Task ARefusal_ReportsNoFactsAtAll()
    {
        var (provider, _) = Build(respond: _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var snapshot = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Null(snapshot.AlwaysOn);
        Assert.Null(snapshot.SiteState);
        Assert.Null(snapshot.Enabled);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task AnUpstreamFailure_IsUnavailable(HttpStatusCode status)
    {
        var (provider, _) = Build(respond: _ => new HttpResponseMessage(status));

        Assert.Equal(AppServiceState.Unavailable, (await provider.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task ABodyWeCannotRead_IsUnavailable_NotACrash()
    {
        var (provider, _) = Build(respond: _ => Json("not json at all"));

        Assert.Equal(AppServiceState.Unavailable, (await provider.GetStatusAsync(CancellationToken.None)).State);
    }

    // A 200 carrying valid JSON that is not an object. TryGetProperty THROWS on those, so without
    // a ValueKind guard this escapes as InvalidOperationException rather than becoming a state --
    // which would 500 the endpoint (where every failure is meant to be a state in the body) and
    // make "Check now" skip both the status and the audit write. Found by the final review; the
    // "not a crash" case above only ever covered bodies that are not JSON at all.
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("\"a string\"")]
    public async Task AValidJsonBodyThatIsNotAnObject_IsUnavailable_NotACrash(string body)
    {
        var (provider, _) = Build(respond: _ => Json(body));

        var snapshot = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AppServiceState.Unavailable, snapshot.State);
        Assert.Null(snapshot.AlwaysOn);
    }

    // The site name is not the only thing that can be missing. Each of the three is required, and
    // each needs its own test: with only the site-name case covered, the other two conditions could
    // be deleted from the guard and every test would still pass.
    [Fact]
    public async Task NoResourceGroup_IsNotConfigured_AndMakesNoCall()
    {
        var (provider, calls) = Build(resourceGroup: null);

        Assert.Equal(AppServiceState.NotConfigured, (await provider.GetStatusAsync(CancellationToken.None)).State);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task NoOwnerName_IsNotConfigured_AndMakesNoCall()
    {
        var (provider, calls) = Build(ownerName: null);

        Assert.Equal(AppServiceState.NotConfigured, (await provider.GetStatusAsync(CancellationToken.None)).State);
        Assert.Empty(calls);
    }

    // A malformed owner name yields no subscription id, which is the same as not having one. It
    // must not be passed through as if it were: that would query a subscription nobody asked about.
    [Fact]
    public async Task AMalformedOwnerName_IsNotConfigured_AndMakesNoCall()
    {
        var (provider, calls) = Build(ownerName: "not-a-subscription+rg-WestEuropewebspace");

        Assert.Equal(AppServiceState.NotConfigured, (await provider.GetStatusAsync(CancellationToken.None)).State);
        Assert.Empty(calls);
    }

    // Pins the ARM path and api-version. Verified against the live resource during the final review,
    // so this is regression cover: a wrong path or a dropped api-version would 404 in production
    // while every behavioural test above still passed, because they stub the response.
    [Fact]
    public async Task TheRequestGoesToTheSitesResourcePath_WithTheApiVersion()
    {
        var (provider, calls) = Build(respond: _ => Json(RealSiteResponse()));

        await provider.GetStatusAsync(CancellationToken.None);

        var request = Assert.Single(calls);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            $"https://management.azure.com/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}" +
            $"/providers/Microsoft.Web/sites/{SiteName}?api-version=2023-12-01",
            request.RequestUri?.ToString());
    }

    // An upstream message can name an internal address; admins read this field in every environment.
    [Fact]
    public async Task AnUpstreamMessage_NeverReachesTheOperatorFacingText()
    {
        var (provider, _) = Build(respond: _ => throw new HttpRequestException("connect failed to 10.0.0.7:443"));

        var snapshot = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AppServiceState.Unavailable, snapshot.State);
        Assert.DoesNotContain("10.0.0.7", snapshot.Message ?? "");
    }

    // A stopped site was read successfully. State says whether we could ASK; SiteState says what the
    // answer was. Collapsing them would make "we could not reach ARM" look like "the site is down".
    [Fact]
    public async Task AStoppedSite_IsOkWithSiteStateStopped()
    {
        var (provider, _) = Build(respond: _ => Json("""
            { "name": "x", "properties": { "state": "Stopped", "enabled": true,
              "siteConfig": { "alwaysOn": false, "numberOfWorkers": 1 } } }
            """));

        var snapshot = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Equal(AppServiceState.Ok, snapshot.State);
        Assert.Equal("Stopped", snapshot.SiteState);
    }
}
