using System.Net;
using System.Net.Http.Json;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Infrastructure.DataSources;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET /admin/health/sources</c> (#460). Unlike <c>GET /admin/health</c> beside it, this runs no
/// health checks at all — it reads the catalog and the status table — so it answers even in a suite
/// where every real check has been cleared.
/// </summary>
[Collection("Integration")]
public class AdminDataSourcesEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public async Task GetSources_Anonymous_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("admin/health/sources");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSources_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var client = factory.CreateUserClient();

        var response = await client.GetAsync("admin/health/sources");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetSources_ReturnsOneRowPerCatalogSource()
    {
        var client = factory.CreateAdminClient();

        var sources = await client.GetFromJsonAsync<List<AdminDataSourceDto>>("admin/health/sources");

        Assert.NotNull(sources);
        Assert.Equal(new DataSourceCatalog().Sources.Count, sources!.Count);
    }

    /// <summary>"Green — but green for what?" is the question this endpoint exists to answer.</summary>
    [Fact]
    public async Task EveryRow_SaysWhatIsChecked()
    {
        var client = factory.CreateAdminClient();

        var sources = await client.GetFromJsonAsync<List<AdminDataSourceDto>>("admin/health/sources");

        Assert.All(sources!, s => Assert.False(string.IsNullOrWhiteSpace(s.WhatIsChecked)));
    }

    /// <summary>
    /// The rule the old AeroAPI check broke by calling a 401 "Healthy": a source nothing has
    /// observed is Unknown, and Unknown is never rendered as healthy.
    /// </summary>
    [Fact]
    public async Task ASourceNothingHasObserved_IsNeverOk()
    {
        var client = factory.CreateAdminClient();

        var sources = await client.GetFromJsonAsync<List<AdminDataSourceDto>>("admin/health/sources");

        var neverObserved = sources!.Where(s => s.LastOutcomeUtc is null);
        Assert.All(neverObserved, s => Assert.NotEqual("Ok", s.Status));
    }
}
