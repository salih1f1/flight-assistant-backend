using System.Net;

namespace FlightAssistant.API.Tests.Integration;

public class AuthEndpointTests(FlightAssistantWebApplicationFactory factory)
    : IClassFixture<FlightAssistantWebApplicationFactory>
{
    [Fact]
    public async Task ProtectedEndpoint_NoToken_Returns401()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("travelers/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoint_UserToken_Returns403()
    {
        var client = factory.CreateUserClient();
        // GET /admin/travelers is an AdminOnly endpoint
        var response = await client.GetAsync("admin/travelers");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoint_AdminToken_DoesNotReturn403()
    {
        var client = factory.CreateAdminClient();
        var response = await client.GetAsync("admin/travelers");
        // May be 200 or another non-403 code; just confirm not forbidden
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
