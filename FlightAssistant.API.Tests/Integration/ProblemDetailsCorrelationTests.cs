using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// #319 promises one correlation id everywhere, and CLAUDE.md states ProblemDetails' <c>traceId</c>
/// carries it. That was true only for problems <see cref="FlightAssistant.API.Middleware.GlobalExceptionHandler"/>
/// writes: a framework-produced problem — minimal-API parameter binding, <c>Results.NotFound()</c>,
/// anything <c>UseStatusCodePages</c> fills in — is written by ASP.NET's own problem writer, which
/// stamps <c>traceId</c> as <c>Activity.Current?.Id ?? HttpContext.TraceIdentifier</c>. Since nothing
/// registers an <c>ActivityListener</c> without Application Insights, that fell through to the
/// connection id, which appears in no log line — so a caller quoting it could not be found (#413).
/// </summary>
[Collection("Integration")]
public class ProblemDetailsCorrelationTests(FlightAssistantWebApplicationFactory factory)
{
    private const string CorrelationIdHeader = "X-Correlation-Id";

    /// <summary>
    /// <c>airportIata</c> is a non-nullable minimal-API query parameter, so omitting it fails
    /// binding before MediatR and before any handler — the one path that cannot reach
    /// <c>GlobalExceptionHandler</c> at all.
    /// </summary>
    [Fact]
    public async Task ABindingFailure400_CarriesTheCallersCorrelationId()
    {
        var correlationId = "test" + Guid.NewGuid().ToString("N");
        var client = factory.CreateUserClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "lounges");
        request.Headers.Add(CorrelationIdHeader, correlationId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The header is the control: it proves the middleware accepted this id for the request.
        Assert.Equal(correlationId, response.Headers.GetValues(CorrelationIdHeader).Single());

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(
            problem.TryGetProperty("traceId", out var traceId),
            "A framework-produced problem document must still carry a traceId.");
        Assert.Equal(correlationId, traceId.GetString());
    }

    /// <summary>
    /// The same guarantee on a problem <c>GlobalExceptionHandler</c> writes, so the two paths are
    /// pinned as agreeing rather than only the newly-fixed one being checked.
    /// </summary>
    [Fact]
    public async Task AHandledValidation400_CarriesTheSameCorrelationId()
    {
        var correlationId = "test" + Guid.NewGuid().ToString("N");
        var client = factory.CreateUserClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "lounges?airportIata=LONDON");
        request.Headers.Add(CorrelationIdHeader, correlationId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(correlationId, problem.GetProperty("traceId").GetString());
    }
}
