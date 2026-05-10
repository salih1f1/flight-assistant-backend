namespace FlightAssistant.API.Tests.Integration;

public class HealthEndpointTests(FlightAssistantWebApplicationFactory factory)
    : IClassFixture<FlightAssistantWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

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
}
