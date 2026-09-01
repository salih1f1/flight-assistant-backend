using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlightAssistant.API.Tests.Integration;

[Collection("Integration")]
public class HealthEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed class FixedHealthCheck(HealthCheckResult result) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    [Fact]
    public async Task Live_AlwaysReturns200()
    {
        var response = await _client.GetAsync("health/live");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_Returns200_WhenNoChecksRegistered()
    {
        // All health checks are removed in the factory; no failing checks = healthy
        var response = await _client.GetAsync("health/ready");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Regression coverage for #311 task 2 (<c>GET /admin/health</c>): that endpoint runs every
    /// check with no predicate, specifically so <c>external</c>-tagged checks like an unhealthy
    /// "aeroapi" are included. This pins that <c>/health/live</c> and <c>/health/ready</c> keep
    /// their own, narrower predicates — unaffected by the new endpoint sharing the same
    /// <c>HealthCheckService</c> registrations.
    /// </summary>
    [Fact]
    public async Task Ready_StillExcludesExternalTaggedChecks_UnaffectedByAdminHealthEndpoint()
    {
        using var scopedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<HealthCheckServiceOptions>(opts =>
                {
                    opts.Registrations.Add(new HealthCheckRegistration(
                        "database",
                        new FixedHealthCheck(HealthCheckResult.Healthy()),
                        failureStatus: null,
                        tags: []));
                    opts.Registrations.Add(new HealthCheckRegistration(
                        "aeroapi",
                        new FixedHealthCheck(HealthCheckResult.Unhealthy("Host unreachable")),
                        failureStatus: null,
                        tags: ["external"]));
                })));
        var client = scopedFactory.CreateClient();

        var readyResponse = await client.GetAsync("health/ready");
        var liveResponse = await client.GetAsync("health/live");

        // /health/ready's predicate excludes "external", so the unhealthy "aeroapi" fake never
        // runs there and the non-external "database" check alone keeps it Healthy/200 — even
        // though the same registrations would push /admin/health's overall status to Unhealthy.
        Assert.Equal(System.Net.HttpStatusCode.OK, readyResponse.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, liveResponse.StatusCode);
    }
}
