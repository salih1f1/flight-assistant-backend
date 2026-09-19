using System.Net;
using System.Net.Http.Json;
using FlightAssistant.Application.Hosting;
using FlightAssistant.Contracts.Responses.Admin;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET /admin/health/app-service</c> hangs off the shared <c>/admin</c> group, same as
/// <c>AdminAzureUsageEndpointTests</c> for the Azure budget — the auth cases here are really
/// checking that it was mapped onto that group rather than standalone.
///
/// <para>
/// No test here substitutes the App Service provider: <c>appsettings.Testing.json</c> sets none of
/// <c>WEBSITE_SITE_NAME</c>/<c>WEBSITE_RESOURCE_GROUP</c>/<c>WEBSITE_OWNER_NAME</c>, so the real
/// provider answers <c>NotConfigured</c> without making any network or credential call — the same
/// reason <c>AdminAzureUsageEndpointTests</c> substitutes nothing for the Azure cost provider.
/// Every other state (Ok, NotAuthorized, Unavailable) is already pinned against a substituted
/// provider in <c>GetAppServiceStatusQueryTests</c>, so this file only has to prove the endpoint is
/// wired to the real one and sits behind the right auth.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminAppServiceEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public async Task GetAppServiceStatus_Anonymous_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("admin/health/app-service");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAppServiceStatus_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var client = factory.CreateUserClient();

        var response = await client.GetAsync("admin/health/app-service");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The state the panel must render as information, not failure, end to end: a 200 carrying
    /// <c>NotConfigured</c> — what an operator's own local dev environment actually sees — rather
    /// than a 4xx/5xx (every failure is a state in the body).
    /// </summary>
    [Fact]
    public async Task GetAppServiceStatus_Admin_Returns200_WithNotConfigured_OnAnUnconfiguredHost()
    {
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/health/app-service");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AppServiceStatusDto>();
        Assert.NotNull(dto);
        Assert.Equal(nameof(AppServiceState.NotConfigured), dto!.State);
        Assert.Null(dto.SiteName);
        Assert.Null(dto.AlwaysOn);
        Assert.NotNull(dto.Message);
    }
}
