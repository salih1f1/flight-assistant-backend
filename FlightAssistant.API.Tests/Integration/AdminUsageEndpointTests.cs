using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Contracts.Responses.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET /admin/usage/aeroapi</c> hangs off the shared <c>/admin</c> group, so the auth cases here
/// are really checking that it was mapped onto that group rather than standalone.
///
/// <para>
/// The AeroAPI provider is always substituted: these tests must never make a live, metered call —
/// and with a placeholder key they could only ever produce a 500 anyway.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminUsageEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private static readonly AeroApiAccountUsage StubUsage = new(
        TotalCalls: 42,
        TotalPages: 46,
        TotalCostUsd: 0.66m,
        TotalDiscountCostUsd: 0m,
        TotalSuccessfulCalls: 41,
        TotalFailedCalls: 1,
        ByOperation: [new AeroApiOperationUsage("GET /schedules/{date_start}/{date_end}", 21, 21, 0.42m, 21, 0)]);

    private WebApplicationFactory<Program> WithStubbedProvider()
    {
        var provider = Substitute.For<IAeroApiAccountUsageProvider>();
        provider
            .GetUsageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(StubUsage);

        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddScoped(_ => provider)));
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
    public async Task GetAeroApiUsage_Anonymous_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("admin/usage/aeroapi");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAeroApiUsage_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var client = factory.CreateUserClient();

        var response = await client.GetAsync("admin/usage/aeroapi");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetAeroApiUsage_Admin_Returns200_WithTheProvidersFiguresAndTheBudget()
    {
        using var scopedFactory = WithStubbedProvider();
        var client = AdminClient(scopedFactory);

        var response = await client.GetAsync("admin/usage/aeroapi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<AeroApiUsageDto>();
        Assert.NotNull(dto);
        Assert.Equal(42, dto!.TotalCalls);
        Assert.Equal(46, dto.TotalPages);
        Assert.Equal(0.66m, dto.TotalCostUsd);
        Assert.Equal(5m, dto.MonthlyBudgetUsd); // AeroApiOptions default — no AeroApi:MonthlyBudgetUsd in Testing config
        Assert.Single(dto.ByOperation);

        // Defaulted range: the current UTC month to date. Asserted structurally rather than by
        // recomputing "the current month" from DateTimeOffset.UtcNow here — that would be flaky
        // across a real month boundary between the request above and this assertion.
        Assert.Equal(1, dto.RangeStartUtc.Day);
        Assert.Equal(TimeSpan.Zero, dto.RangeStartUtc.TimeOfDay);
        Assert.Equal(TimeSpan.Zero, dto.RangeStartUtc.Offset);
    }

    [Fact]
    public async Task GetAeroApiUsage_StartOlderThanOneYear_Returns400()
    {
        using var scopedFactory = WithStubbedProvider();
        var client = AdminClient(scopedFactory);
        var tooOld = DateTimeOffset.UtcNow.AddYears(-1).AddDays(-2).ToString("yyyy-MM-ddTHH:mm:ssZ");

        var response = await client.GetAsync($"admin/usage/aeroapi?start={Uri.EscapeDataString(tooOld)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("1 year", body);
    }
}
