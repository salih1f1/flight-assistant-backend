using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// Scope enforcement used to sit in each handler as <c>VerifyUserHasAnyAcceptedScope</c>, which
/// commits a 403 and <em>then</em> throws. Because the response had already started,
/// <c>UseExceptionHandler</c> declined to run ("the response has already started, the error handler
/// will not be executed"), the exception escaped the whole pipeline, and Serilog's request logging
/// — which logs a hardcoded 500 for any exception passing through it — recorded the request as a
/// 500. The client got a 403 with no body and no content type; the log said 500 (#413).
///
/// <para>
/// It is now the <c>AccessAsUser</c> authorization policy on each traveler group, so the check runs
/// before the handler, nothing has started, and the 403 is an ordinary authorization result that
/// <c>UseStatusCodePages</c> gives a problem document.
/// </para>
/// </summary>
[Collection("Integration")]
public class ScopeEnforcementTests(FlightAssistantWebApplicationFactory factory)
{
    public static TheoryData<string> TravelerRoutes() =>
    [
        "flights",
        "lounges?airportIata=LHR",
        "loyalty/programs",
        "travelers/me",
    ];

    [Theory]
    [MemberData(nameof(TravelerRoutes))]
    public async Task ATokenWithoutTheScope_IsForbiddenOnEveryTravelerGroup(string route)
    {
        var response = await factory.CreateScopelessUserClient().GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The 403 must be a problem document like every other error this API returns, not the bodyless
    /// response the old per-handler check produced.
    /// </summary>
    [Fact]
    public async Task TheForbiddenResponse_IsAProblemDocumentCarryingTheCorrelationId()
    {
        var correlationId = "test" + Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Get, "flights");
        request.Headers.Add("X-Correlation-Id", correlationId);

        var response = await factory.CreateScopelessUserClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
        Assert.Equal(correlationId, problem.GetProperty("traceId").GetString());
    }

    /// <summary>A token that does carry the scope is unaffected — the guard against the policy
    /// being wrong in the other direction and locking every traveler out.</summary>
    [Fact]
    public async Task ATokenWithTheScope_IsStillAdmitted()
    {
        var response = await factory.CreateUserClient().GetAsync("flights");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
