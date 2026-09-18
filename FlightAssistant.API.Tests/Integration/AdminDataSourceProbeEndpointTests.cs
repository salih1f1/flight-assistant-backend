using System.Net;
using System.Net.Http.Json;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Infrastructure.DataSources;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>POST /admin/health/sources/{key}/probe</c> (#460): the one place this feature starts traffic
/// of its own, so every guard around it is asserted — who may call it, which sources accept one,
/// that it is audited, and that it cannot be pressed in a loop.
///
/// <para>
/// Each test uses its own admin id: the rate limiter partitions by admin and source, and its state
/// is shared with every other test in the Integration collection.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminDataSourceProbeEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private static string UniqueAdminId() => $"probe-admin-{Guid.NewGuid():N}";

    private const string Postgres = DataSourceKeys.Postgres;

    [Fact]
    public async Task Probe_Anonymous_Returns401()
    {
        var response = await factory.CreateClient().PostAsync($"admin/health/sources/{Postgres}/probe", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Probe_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var response = await factory.CreateUserClient().PostAsync($"admin/health/sources/{Postgres}/probe", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Probe_ReturnsTheRefreshedRow()
    {
        var client = factory.CreateAdminClient(UniqueAdminId());

        var response = await client.PostAsync($"admin/health/sources/{Postgres}/probe", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await response.Content.ReadFromJsonAsync<AdminDataSourceDto>();
        Assert.Equal(Postgres, row!.Key);
        Assert.NotNull(row.LastProbeUtc);
        Assert.Equal("Ok", row.LastOutcome);
    }

    [Fact]
    public async Task Probe_RejectsAnUnknownSource()
    {
        var client = factory.CreateAdminClient(UniqueAdminId());

        var response = await client.PostAsync("admin/health/sources/not-a-source/probe", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>A source with no probe must refuse, rather than quietly doing nothing or spending money.</summary>
    [Fact]
    public async Task Probe_RejectsASourceThatDoesNotSupportOne()
    {
        var client = factory.CreateAdminClient(UniqueAdminId());

        var response = await client.PostAsync($"admin/health/sources/{DataSourceKeys.GitHub}-download/probe", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The catalog's answer ("this source has no probe"), not the registry's ("nobody registered
        // one"): the two are both 400, so without this the SupportsProbe guard could be deleted and
        // every assertion here would still pass.
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("cannot be checked on demand", problem!.Title);
    }

    [Fact]
    public async Task Probe_WritesAnAuditRow()
    {
        var adminId = UniqueAdminId();
        var client = factory.CreateAdminClient(adminId);

        await client.PostAsync($"admin/health/sources/{Postgres}/probe", null);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var audit = await db.AdminAuditLogs
            .Where(a => a.ActorId == adminId)
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.Contains(Postgres, audit.Summary);
    }

    [Fact]
    public async Task Probe_RefusesASecondCallInsideTheRateLimitWindow()
    {
        var client = factory.CreateAdminClient(UniqueAdminId());

        var first = await client.PostAsync($"admin/health/sources/{Postgres}/probe", null);
        var second = await client.PostAsync($"admin/health/sources/{Postgres}/probe", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    /// <summary>
    /// The limiter partitions by source as well as admin, so checking one source never locks an
    /// operator out of checking the next one while they are working through the page.
    /// </summary>
    [Fact]
    public async Task Probe_OfADifferentSource_IsNotRateLimitedByTheFirst()
    {
        var client = factory.CreateAdminClient(UniqueAdminId());

        await client.PostAsync($"admin/health/sources/{Postgres}/probe", null);
        var other = await client.PostAsync($"admin/health/sources/{DataSourceKeys.AppInsights}/probe", null);

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }
}
