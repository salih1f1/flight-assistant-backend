using System.Net;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// Pins that <c>UseStatusCodePages()</c> (<c>Program.cs</c>), paired with
/// <c>AddProblemDetails()</c>, is what gives a framework-produced error a body at all (#415):
/// the originating code (routing, model binding, the auth policies) sets a status and writes
/// nothing, and this middleware fills a problem document in afterwards. Without it, those
/// responses go out bodyless with no content type even though their status code is correct.
/// </summary>
[Collection("Integration")]
public class StatusCodePagesTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public async Task AFrameworkProducedError_CarriesAProblemBody_NotJustAStatus()
    {
        // UseStatusCodePages() + AddProblemDetails() is what gives framework errors (routing 404,
        // the auth policies, model binding) a body at all; without it they go out bodyless with no
        // content type. Deleting either as "unused" should fail here, loudly (#415).
        var response = await factory.CreateAdminClient().GetAsync("admin/no-such-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsStringAsync());
    }
}
