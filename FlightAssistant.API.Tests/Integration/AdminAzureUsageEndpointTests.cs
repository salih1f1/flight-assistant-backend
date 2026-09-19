using System.Net;
using System.Net.Http.Json;
using FlightAssistant.Application.Usage;
using FlightAssistant.Contracts.Responses.Admin;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET /admin/usage/azure</c> hangs off the shared <c>/admin</c> group, same as
/// <c>AdminUsageEndpointTests</c> for AeroAPI — the auth cases here are really checking that it was
/// mapped onto that group rather than standalone.
///
/// <para>
/// No test here substitutes the Azure cost provider: <c>appsettings.Testing.json</c> sets no
/// <c>AzureCost:SubscriptionId</c>, so the real provider answers <c>NotConfigured</c> without
/// making any network or credential call — the same reason <c>DataSourceCatalogTests</c> can build
/// the real container safely. Every other state (Ok, NoBudget, NotAuthorized, Unavailable) is
/// already pinned against a substituted provider in <c>GetAzureBudgetQueryTests</c>, so this file
/// only has to prove the endpoint is wired to the real one and sits behind the right auth.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminAzureUsageEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public async Task GetAzureBudget_Anonymous_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("admin/usage/azure");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAzureBudget_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var client = factory.CreateUserClient();

        var response = await client.GetAsync("admin/usage/azure");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The state the panel must render as information, not failure, end to end: a 200 carrying
    /// <c>NotConfigured</c> — what an operator's own local dev environment actually sees — rather
    /// than a 4xx/5xx (#496 task 4: every failure is a state in the body).
    /// </summary>
    [Fact]
    public async Task GetAzureBudget_Admin_Returns200_WithNotConfigured_OnAnUnconfiguredHost()
    {
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/usage/azure");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AzureBudgetDto>();
        Assert.NotNull(dto);
        Assert.Equal(nameof(AzureBudgetState.NotConfigured), dto!.State);
        Assert.Null(dto.BudgetAmount);
        Assert.Null(dto.CurrentSpendAmount);
        Assert.Null(dto.DataAsOfUtc);
        Assert.NotNull(dto.Message);
    }
}
