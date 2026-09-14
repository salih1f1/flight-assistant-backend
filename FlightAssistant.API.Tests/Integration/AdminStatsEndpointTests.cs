using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.Contracts.Responses.Admin;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET /admin/stats</c> (#336) hangs off the shared <c>/admin</c> group, so the auth cases check
/// it was mapped there. The "Integration" collection shares one InMemory database across every test
/// class, so these assert the response's shape and the window, never particular counts. Those are
/// covered against real PostgreSQL in <c>AdminStatsRepositoryTests</c>.
/// </summary>
[Collection("Integration")]
public class AdminStatsEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private static readonly string[] Cards = ["travelers", "flights", "airlines", "airports", "lounges", "loyalty-programs"];

    [Fact]
    public async Task GetStats_Anonymous_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("admin/stats");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetStats_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var response = await factory.CreateUserClient().GetAsync("admin/stats");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetStats_Admin_DefaultsToSevenDailyBuckets_ForTheSixCards()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stats = await response.Content.ReadFromJsonAsync<AdminStatsDto>();
        Assert.NotNull(stats);
        Assert.Equal(("7d", "day"), (stats!.Period, stats.Bucket));
        Assert.Equal(TimeSpan.FromDays(7), stats.ToUtc - stats.FromUtc);
        Assert.Equal(TimeSpan.Zero, stats.FromUtc.TimeOfDay);
        Assert.Equal(Cards, stats.Entities.Select(e => e.Key));
        Assert.All(stats.Entities, e =>
        {
            Assert.Equal(7, e.Series.Count);
            Assert.Equal(e.Series.Sum(), e.NewInPeriod);
        });
    }

    [Fact]
    public async Task GetStats_24h_HasTwentyFourHourlyBuckets()
    {
        var stats = await factory.CreateAdminClient().GetFromJsonAsync<AdminStatsDto>("admin/stats?period=24h");

        Assert.Equal(("24h", "hour"), (stats!.Period, stats.Bucket));
        Assert.Equal(TimeSpan.FromHours(24), stats.ToUtc - stats.FromUtc);
        Assert.Equal((0, 0), (stats.FromUtc.Minute, stats.FromUtc.Second));
        Assert.All(stats.Entities, e => Assert.Equal(24, e.Series.Count));
    }

    [Fact]
    public async Task GetStats_UnknownPeriod_Returns400_UnderThePeriodKey()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/stats?period=1y");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("period", out _));
    }
}
