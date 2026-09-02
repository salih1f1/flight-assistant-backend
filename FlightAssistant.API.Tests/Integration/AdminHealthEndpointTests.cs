using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.API.HostedServices;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// Repoints <see cref="FlightAssistantDbContext"/> at a real (Npgsql) relational provider aimed
    /// at a dead endpoint, replacing the base factory's InMemory provider — the only way to
    /// exercise "relational but unreachable", since <c>IsRelational()</c> alone answers <c>true</c>
    /// for Npgsql regardless of whether Postgres is actually reachable. Mirrors the EF-Core-assembly
    /// stripping <see cref="FlightAssistantWebApplicationFactory"/> itself does when swapping in
    /// InMemory: EF's internal service provider is keyed on every registered database provider's
    /// options, so the InMemory registration must be fully removed, not just shadowed by adding
    /// Npgsql alongside it, or EF throws "Services for database providers ... have been registered".
    /// </summary>
    private static WebApplicationFactory<Program> WithDeadRelationalDatabase(
        FlightAssistantWebApplicationFactory factory, string? environmentName = null)
    {
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                if (environmentName is not null)
                    services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environmentName));

                var efCoreAssemblyPrefixes = new[]
                {
                    "Microsoft.EntityFrameworkCore",
                    "Npgsql.EntityFrameworkCore",
                };
                var toRemove = services
                    .Where(d =>
                    {
                        if (d.ServiceType == typeof(FlightAssistantDbContext)) return true;
                        var svcAssembly = d.ServiceType.Assembly.GetName().Name;
                        if (svcAssembly is not null && efCoreAssemblyPrefixes.Any(p => svcAssembly.StartsWith(p)))
                            return true;
                        var implAssembly = d.ImplementationType?.Assembly.GetName().Name;
                        if (implAssembly is not null && efCoreAssemblyPrefixes.Any(p => implAssembly.StartsWith(p)))
                            return true;
                        return false;
                    })
                    .ToList();
                foreach (var d in toRemove)
                    services.Remove(d);

                // Port 1 on loopback: nothing listens there, so the connection is refused
                // immediately rather than hanging out the full Npgsql default timeout; the explicit
                // Timeout/Command Timeout bound the worst case regardless.
                services.AddDbContext<FlightAssistantDbContext>(opt =>
                    opt.UseNpgsql("Host=127.0.0.1;Port=1;Timeout=2;Command Timeout=2"));
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

        // Nothing is configured for Redis/AppInsights in appsettings.Testing.json — the booleans
        // must reflect that, not default to a stale "true".
        Assert.False(dto.System.RedisConfigured);
        Assert.False(dto.System.AppInsightsConfigured);
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
        Assert.Equal("Non-relational database provider.", dto.Migrations.Error);
    }

    [Fact]
    public async Task GetHealth_DatabaseUnreachable_Returns200_WithMigrationsUnavailable_NotA500()
    {
        // Regression test for final-review finding C1: IsRelational() answers a *provider*
        // question (true for Npgsql whether or not Postgres is reachable), so it alone doesn't
        // guard against a live-but-unreachable database — GetAppliedMigrationsAsync and
        // GetPendingMigrationsAsync need a real connection and threw a raw NpgsqlException that
        // took the whole report down with it (confirmed against exactly this dead-endpoint setup
        // during review). BuildMigrationsAsync must degrade to Available: false instead, the same
        // "cannot tell" shape the non-relational guard already produces — not 500.
        using var scopedFactory = WithDeadRelationalDatabase(factory);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);
        Assert.False(dto!.Migrations.Available);
        Assert.Equal(0, dto.Migrations.AppliedCount);
        Assert.Null(dto.Migrations.LastAppliedMigration);
        Assert.Empty(dto.Migrations.Pending);
        // Testing (the ambient, non-overridden environment here) is non-Production, so the reason
        // renders.
        Assert.NotNull(dto.Migrations.Error);
    }

    [Fact]
    public async Task GetHealth_DatabaseUnreachable_Production_NullsTheMigrationsError()
    {
        using var scopedFactory = WithDeadRelationalDatabase(factory, environmentName: Environments.Production);
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<AdminHealthDto>();
        Assert.NotNull(dto);
        Assert.False(dto!.Migrations.Available);
        Assert.Null(dto.Migrations.Error);
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
        // Note on what this test does and doesn't prove: it overrides IHostEnvironment to a
        // concretely-named "Development" for readability, but Testing (the ambient environment
        // this whole suite already runs under) is itself non-Production — so this assertion
        // passes identically with the override removed and does not by itself demonstrate that
        // the override mechanism works. GetHealth_Production_NullsTheErrorForTheThrowingCheck is
        // the one that's discriminating (Testing would fail that assertion without its override),
        // and the pair together prove both that population happens outside Production and that
        // null in Production isn't merely an absent field.
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
