using System.Net;
using System.Text;
using System.Text.Json;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// AeroAPI signals failures with real HTTP status codes (no <c>error</c> body under HTTP 200 the
/// way AviationStack does), so these tests drive <see cref="AeroApiResponse.ReadOrThrowAsync"/>
/// directly against <see cref="HttpResponseMessage"/> instances rather than a stubbed client.
/// </summary>
public class AeroApiResponseTests
{
    private const string SecretKey = "super-secret-aeroapi-key";

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

    [Fact]
    public async Task ReadOrThrowAsync_ReturnsDocument_On200()
    {
        using var response = Response(HttpStatusCode.OK, """{ "scheduled": [] }""");

        using var doc = await AeroApiResponse.ReadOrThrowAsync(
            response, "Flight search", "SOF-IST 2026-10-10", NullLogger.Instance, CancellationToken.None);

        Assert.True(doc.RootElement.TryGetProperty("scheduled", out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ReadOrThrowAsync_Throws500_OnCredentialFailure(HttpStatusCode status)
    {
        using var response = Response(status, $$"""{ "title": "unauthorized", "reason": "bad key {{SecretKey}}" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            AeroApiResponse.ReadOrThrowAsync(
                response, "Flight search", "SOF-IST 2026-10-10", NullLogger.Instance, CancellationToken.None));

        Assert.Equal(500, ex.StatusCode);
        Assert.Equal("Flight data is temporarily unavailable.", ex.Message);
        Assert.DoesNotContain(SecretKey, ex.Message);
        Assert.Contains(SecretKey, ex.DiagnosticDetail);
    }

    [Fact]
    public async Task ReadOrThrowAsync_Throws503_OnThrottling_WithoutRetryAfterHeader()
    {
        using var response = Response(HttpStatusCode.TooManyRequests, """{ "title": "too many requests" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            AeroApiResponse.ReadOrThrowAsync(
                response, "Flight search", "SOF-IST 2026-10-10", NullLogger.Instance, CancellationToken.None));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("Flight search is busy. Please try again shortly.", ex.Message);
        Assert.Contains("429", ex.DiagnosticDetail);
    }

    [Fact]
    public async Task ReadOrThrowAsync_Throws503_OnThrottling_AndSurfacesRetryAfterInMessage()
    {
        using var response = Response(HttpStatusCode.TooManyRequests, """{ "title": "too many requests" }""", retryAfterSeconds: "30");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            AeroApiResponse.ReadOrThrowAsync(
                response, "Flight search", "SOF-IST 2026-10-10", NullLogger.Instance, CancellationToken.None));

        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("Retry-After: 30s", ex.Message);
    }

    [Fact]
    public async Task ReadOrThrowAsync_Throws400_OnBadRequest()
    {
        using var response = Response(HttpStatusCode.BadRequest, $$"""{ "title": "invalid parameter", "detail": "{{SecretKey}}" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            AeroApiResponse.ReadOrThrowAsync(
                response, "Flight search", "SOF-IST 2026-10-10", NullLogger.Instance, CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("The flight search request was rejected.", ex.Message);
        Assert.DoesNotContain(SecretKey, ex.Message);
        Assert.Contains(SecretKey, ex.DiagnosticDetail);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ReadOrThrowAsync_Throws502_OnUnclassifiedFailure(HttpStatusCode status)
    {
        using var response = Response(status, $$"""{ "title": "unexpected", "detail": "{{SecretKey}}" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            AeroApiResponse.ReadOrThrowAsync(
                response, "Flight search", "SOF-IST 2026-10-10", NullLogger.Instance, CancellationToken.None));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("Flight search is temporarily unavailable. Please try again shortly.", ex.Message);
        Assert.DoesNotContain(SecretKey, ex.Message);
        Assert.Contains(SecretKey, ex.DiagnosticDetail);
    }

    [Fact]
    public async Task ReadOrThrowAsync_Throws502_OnUnparseableSuccessBody()
    {
        using var response = Response(HttpStatusCode.OK, "not json");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            AeroApiResponse.ReadOrThrowAsync(
                response, "Flight search", "SOF-IST 2026-10-10", NullLogger.Instance, CancellationToken.None));

        Assert.Equal(502, ex.StatusCode);
        Assert.DoesNotContain("not json", ex.Message);
    }

    [Fact]
    public async Task ReadOrThrowAsync_NeverLeaksApiKey_EvenWhenBodyContainsIt()
    {
        // Defensive: AeroAPI never echoes the key back in a response body (it only ever travels in
        // the x-apikey request header), but if it ever did, Message must still stay clean.
        using var response = Response(HttpStatusCode.TooManyRequests, $$"""{ "detail": "key {{SecretKey}} exceeded quota" }""");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            AeroApiResponse.ReadOrThrowAsync(
                response, "Flight search", "SOF-IST 2026-10-10", NullLogger.Instance, CancellationToken.None));

        Assert.DoesNotContain(SecretKey, ex.Message);
        Assert.Contains(SecretKey, ex.DiagnosticDetail);
    }
}
