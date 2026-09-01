using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlightAssistant.Contracts.Responses.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET /admin/health</c> hangs off the shared <c>/admin</c> group, so the auth cases here are
/// really checking that it was mapped onto that group rather than standalone.
///
/// <para>
/// The base factory clears every registered <c>IHealthCheck</c> (the real DB/Redis/APNs/AeroAPI
/// checks all need infra this suite doesn't have), so most scenarios here register their own fake
/// checks via <c>WithWebHostBuilder</c> instead.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminHealthEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private sealed class FixedHealthCheck(HealthCheckResult result) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    /// <summary>
    /// Swaps in a fake <c>IHostEnvironment</c> without touching the process's real
    /// <c>ASPNETCORE_ENVIRONMENT</c> (which stays "Testing" so the host's own required
    /// configuration — Postgres, Azure AD, AeroAPI — keeps resolving from
    /// appsettings.Testing.json). Only <c>AdminHealthReportBuilder</c>'s
    /// <c>env.IsProduction()</c> branch needs to see a different name.
    /// </summary>
    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "FlightAssistant.API.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static readonly InvalidOperationException ApnsException = new("boom");

    private static WebApplicationFactory<Program> WithThreeFakeChecks(
        FlightAssistantWebApplicationFactory factory, string? environmentName = null)
    {
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                if (environmentName is not null)
                    services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environmentName));

                services.Configure<HealthCheckServiceOptions>(opts =>
                {
                    opts.Registrations.Add(new HealthCheckRegistration(
                        "database",
                        new FixedHealthCheck(HealthCheckResult.Healthy()),
                        failureStatus: null,
                        tags: []));
                    opts.Registrations.Add(new HealthCheckRegistration(
                        "aeroapi",
                        new FixedHealthCheck(HealthCheckResult.Degraded("Host unreachable")),
                        failureStatus: null,
                        tags: ["external"]));
                    opts.Registrations.Add(new HealthCheckRegistration(
                        "apns",
                        new FixedHealthCheck(HealthCheckResult.Unhealthy("APNs check threw", ApnsException)),
                        failureStatus: null,
                        tags: ["external"]));
                });
            }));
    }

    /// <summary>WithWebHostBuilder hands back the base factory type, so the derived factory's
    /// CreateAdminClient convenience is out of reach — same token shape, built here.</summary>
    private static HttpClient AdminClient(WebApplicationFactory<Program> scopedFactory)
    {
        var client = scopedFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", $"test-admin-{Guid.NewGuid()}");
        return client;
    }

    [Fact]
    public async Task GetHealth_Anonymous_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetHealth_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var client = factory.CreateUserClient();

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetHealth_Admin_NoChecksRegistered_Returns200_HealthyWithEmptyChecks()
    {
        // The base factory clears every health check and never re-adds any: the "nothing
        // registered" edge case must render as Healthy with an empty list, not a 500.
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);
        Assert.Equal("Healthy", dto!.Status);
        Assert.Empty(dto.Checks);
    }

    [Fact]
    public async Task GetHealth_Admin_InMemoryProvider_MigrationsAvailableIsFalse_AndDoesNotThrow()
    {
        // FlightAssistantWebApplicationFactory swaps in the EF Core InMemory provider, which
        // throws on GetPendingMigrationsAsync/GetAppliedMigrationsAsync. "Available: false" is a
        // distinct answer from "no migrations pending" — the request must not throw either way.
        var client = factory.CreateAdminClient();

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);
        Assert.False(dto!.Migrations.Available);
        Assert.Equal(0, dto.Migrations.AppliedCount);
        Assert.Null(dto.Migrations.LastAppliedMigration);
        Assert.Empty(dto.Migrations.Pending);
    }

    [Fact]
    public async Task GetHealth_PerCheckShape_ReturnsNameStatusDescriptionTagsAndWorstStatusRollup()
    {
        using var scopedFactory = WithThreeFakeChecks(factory);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);

        // Unhealthy (apns) beats Degraded (aeroapi) beats Healthy (database).
        Assert.Equal("Unhealthy", dto!.Status);
        Assert.Equal(3, dto.Checks.Count);

        var database = Assert.Single(dto.Checks, c => c.Name == "database");
        Assert.Equal("Healthy", database.Status);
        Assert.Empty(database.Tags);

        var aeroapi = Assert.Single(dto.Checks, c => c.Name == "aeroapi");
        Assert.Equal("Degraded", aeroapi.Status);
        Assert.Equal("Host unreachable", aeroapi.Description);
        Assert.Contains("external", aeroapi.Tags);

        var apns = Assert.Single(dto.Checks, c => c.Name == "apns");
        Assert.Equal("Unhealthy", apns.Status);
        Assert.Equal("APNs check threw", apns.Description);
        Assert.Contains("external", apns.Tags);
    }

    [Fact]
    public async Task GetHealth_Development_PopulatesErrorForTheThrowingCheck()
    {
        using var scopedFactory = WithThreeFakeChecks(factory, environmentName: Environments.Development);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);

        var apns = Assert.Single(dto!.Checks, c => c.Name == "apns");
        Assert.NotNull(apns.Error);
        Assert.Contains("InvalidOperationException", apns.Error);
        Assert.Contains("boom", apns.Error);
    }

    [Fact]
    public async Task GetHealth_Production_NullsTheErrorForTheThrowingCheck()
    {
        // The security-relevant assertion in this set: exception detail must never reach a
        // Production caller, even for the check that actually threw.
        using var scopedFactory = WithThreeFakeChecks(factory, environmentName: Environments.Production);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);

        var apns = Assert.Single(dto!.Checks, c => c.Name == "apns");
        Assert.Null(apns.Error);

        // Description is author-written health-check text, not exception detail — it still
        // renders even in Production.
        Assert.Equal("APNs check threw", apns.Description);
    }
}
