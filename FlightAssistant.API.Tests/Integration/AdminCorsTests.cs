using Microsoft.AspNetCore.Hosting;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// The admin panel is served from a different origin than the API, so every admin call is
/// cross-origin and dies at preflight if its origin is not configured. One origin is not enough:
/// a developer's <c>http://localhost:5175</c> and the deployed panel both have to work, and
/// <c>appsettings.Development.json</c> ships to the dev App Service (the deployed API also runs as
/// <c>Development</c>), so a single value cannot hold both without breaking local development the
/// moment it deploys (#535).
///
/// These tests drive the real CORS middleware through a preflight request rather than inspecting
/// configuration: reading the setting back would pass just as happily if nothing consumed it.
/// </summary>
public class AdminCorsTests : IClassFixture<AdminCorsTests.TwoOriginFactory>
{
    private readonly TwoOriginFactory _factory;

    public AdminCorsTests(TwoOriginFactory factory) => _factory = factory;

    public class TwoOriginFactory : FlightAssistantWebApplicationFactory
    {
        public const string LocalOrigin = "http://localhost:5175";
        public const string DeployedOrigin = "https://flight-assistant-dev-panel.azurewebsites.net";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // UseSetting, not ConfigureAppConfiguration: Program.cs reads this key while the builder
            // is still being assembled, and ConfigureAppConfiguration is applied after Build() — the
            // same ordering trap the base factory documents for UseEnvironment. Array indices are
            // how a list reaches configuration through flat key/value settings.
            builder.UseSetting("AdminPanel:AllowedOrigins:0", LocalOrigin);
            builder.UseSetting("AdminPanel:AllowedOrigins:1", DeployedOrigin);
        }
    }

    private async Task<HttpResponseMessage> Preflight(string origin)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/admin/airlines");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData(TwoOriginFactory.LocalOrigin)]
    [InlineData(TwoOriginFactory.DeployedOrigin)]
    public async Task EveryConfiguredOriginIsAllowed(string origin)
    {
        var response = await Preflight(origin);

        Assert.True(
            response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed),
            $"Preflight from the configured origin {origin} returned no Access-Control-Allow-Origin " +
            "header, so a browser will refuse every admin call made from it.");
        Assert.Equal(origin, Assert.Single(allowed!));
    }

    [Fact]
    public async Task AnUnconfiguredOriginIsRefused()
    {
        var response = await Preflight("https://example.invalid");

        Assert.False(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "An origin that is not configured must not be echoed back; otherwise the list is " +
            "decorative and any site could call the admin API from a browser.");
    }
}
