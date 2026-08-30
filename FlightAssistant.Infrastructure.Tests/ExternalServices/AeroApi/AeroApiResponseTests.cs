using System.Net;
using System.Text;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// AeroAPI signals failures with real HTTP status codes (no <c>error</c> body under HTTP 200), so
/// these tests drive <see cref="AeroApiResponse.ReadOrThrowAsync"/> directly against
/// <see cref="HttpResponseMessage"/> instances rather than a stubbed client.
///
/// <see cref="AeroApiResponse"/> is shared by every AeroAPI caller (future flight search, future
/// flight status), so <c>operation</c> is varied across these tests to prove the client-facing
/// wording generalises rather than hardcoding "flight search".
/// </summary>
public class AeroApiResponseTests
{
    // Stands in for the configured AeroApi:ApiKey — must never survive into Message or
    // DiagnosticDetail, planted directly in response bodies below to prove that.
    private const string ConfiguredApiKey = "super-secret-aeroapi-key";

    private static HttpResponseMessage Response(HttpStatusCode status, string body, string? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        if (retryAfterSeconds is not null)
        {
            response.Headers.Add("Retry-After", retryAfterSeconds);
        }

        return response;
    }

    private static Task<System.Text.Json.JsonDocument> Read(
        HttpResponseMessage response, string operation = "Flight search", string criteria = "SOF-IST 2026-10-10") =>
        AeroApiResponse.ReadOrThrowAsync(
            response, operation, criteria, ConfiguredApiKey, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task ReadOrThrowAsync_ReturnsDocument_On200()
    {
        using var response = Response(HttpStatusCode.OK, """{ "scheduled": [] }""");

        using var doc = await Read(response);

        Assert.True(doc.RootElement.TryGetProperty("scheduled", out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ReadOrThrowAsync_Throws500_OnCredentialFailure(HttpStatusCode status)
    {
        using var response = Response(status, $$"""{ "title": "unauthorized", "reason": "bad key {{ConfiguredApiKey}}" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Read(response));

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("Flight data is temporarily unavailable.", ex.Message);
        Assert.DoesNotContain(ConfiguredApiKey, ex.Message);
        Assert.DoesNotContain(ConfiguredApiKey, ex.DiagnosticDetail);
    }

    [Fact]
    public async Task ReadOrThrowAsync_Throws503_OnThrottling_WithoutRetryAfterHeader()
    {
        using var response = Response(HttpStatusCode.TooManyRequests, """{ "title": "too many requests" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Read(response));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("Flight search is busy. Please try again shortly.", ex.Message);
        Assert.Contains("429", ex.DiagnosticDetail);
    }

    [Fact]
    public async Task ReadOrThrowAsync_Throws503_OnThrottling_AndSurfacesRetryAfterInMessage()
    {
        using var response = Response(HttpStatusCode.TooManyRequests, """{ "title": "too many requests" }""", retryAfterSeconds: "30");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Read(response));

        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("Retry-After: 30s", ex.Message);
    }

    [Fact]
    public async Task ReadOrThrowAsync_Throws400_OnBadRequest()
    {
        using var response = Response(HttpStatusCode.BadRequest, $$"""{ "title": "invalid parameter", "detail": "{{ConfiguredApiKey}}" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Read(response));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("The flight search request was rejected.", ex.Message);
        Assert.DoesNotContain(ConfiguredApiKey, ex.Message);
        Assert.DoesNotContain(ConfiguredApiKey, ex.DiagnosticDetail);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ReadOrThrowAsync_Throws502_OnUnclassifiedFailure(HttpStatusCode status)
    {
        using var response = Response(status, $$"""{ "title": "unexpected", "detail": "{{ConfiguredApiKey}}" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Read(response));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("Flight search is temporarily unavailable. Please try again shortly.", ex.Message);
        Assert.DoesNotContain(ConfiguredApiKey, ex.Message);
        Assert.DoesNotContain(ConfiguredApiKey, ex.DiagnosticDetail);
    }

    [Fact]
    public async Task ReadOrThrowAsync_Throws502_OnUnparseableSuccessBody()
    {
        using var response = Response(HttpStatusCode.OK, "not json");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Read(response));

        Assert.Equal(502, ex.StatusCode);
        Assert.DoesNotContain("not json", ex.Message);
    }

    [Fact]
    public async Task ReadOrThrowAsync_RedactsConfiguredApiKey_FromMessageAndDiagnosticDetail()
    {
        // The key must never survive anywhere client- or operator-visible, even outside Production
        // where DiagnosticDetail is rendered — while the rest of the diagnostic context (the part
        // that isn't the secret) must still make it through so operators can actually debug.
        using var response = Response(
            HttpStatusCode.TooManyRequests,
            $$"""{ "title": "quota exceeded", "detail": "key {{ConfiguredApiKey}} exceeded quota" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Read(response));

        Assert.DoesNotContain(ConfiguredApiKey, ex.Message);
        Assert.DoesNotContain(ConfiguredApiKey, ex.DiagnosticDetail);
        Assert.Contains("[REDACTED]", ex.DiagnosticDetail);
        Assert.Contains("quota exceeded", ex.DiagnosticDetail); // non-secret context survives
    }

    [Fact]
    public async Task ReadOrThrowAsync_RedactsCredentialShapedFields_EvenWhenNotTheConfiguredKey()
    {
        // Defense in depth: a credential-shaped field is redacted on its shape alone, independent
        // of whether it happens to match the key this process was configured with.
        const string unrelatedToken = "totally-different-token-value";
        using var response = Response(
            HttpStatusCode.TooManyRequests,
            $$"""{ "title": "quota exceeded", "apikey": "{{unrelatedToken}}" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Read(response));

        Assert.DoesNotContain(unrelatedToken, ex.DiagnosticDetail);
        Assert.Contains("[REDACTED]", ex.DiagnosticDetail);
        Assert.Contains("quota exceeded", ex.DiagnosticDetail);
    }

    [Fact]
    public async Task ReadOrThrowAsync_GeneralisesWording_ForANonSearchOperation()
    {
        // AeroApiResponse is shared with the future flight-status provider (Task 3) — a status
        // lookup failing must not tell the caller their *search* failed.
        using var response = Response(HttpStatusCode.TooManyRequests, """{ "title": "too many requests" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Read(response, operation: "Flight status"));

        Assert.Equal("Flight status is busy. Please try again shortly.", ex.Message);
        Assert.DoesNotContain("search", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadOrThrowAsync_GeneralisesBadRequestWording_ForANonSearchOperation()
    {
        using var response = Response(HttpStatusCode.BadRequest, """{ "title": "invalid parameter" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Read(response, operation: "Flight status"));

        Assert.Equal("The flight status request was rejected.", ex.Message);
    }
}
