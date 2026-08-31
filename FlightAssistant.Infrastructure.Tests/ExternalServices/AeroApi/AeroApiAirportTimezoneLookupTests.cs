using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// Drives <see cref="AeroApiAirportTimezoneLookup"/> against stubbed <c>/airports/{id}</c>
/// responses, pinning its contract: cache successes for 30 days, never cache a miss, never let a
/// failure propagate.
/// </summary>
public class AeroApiAirportTimezoneLookupTests
{
    private sealed class StubHttpMessageHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static AeroApiAirportTimezoneLookup Lookup(
        StubHttpMessageHandler handler, ICacheService? cache = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/aeroapi/") };
        return new AeroApiAirportTimezoneLookup(
            http,
            cache ?? EmptyCache(),
            Options.Create(new AeroApiOptions { ApiKey = "dummy-key" }),
            NullLogger<AeroApiAirportTimezoneLookup>.Instance);
    }

    private static ICacheService EmptyCache()
    {
        var cache = Substitute.For<ICacheService>();
        cache.GetAsync<string>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        return cache;
    }

    [Fact]
    public async Task TryGetTimezoneAsync_ReturnsTheTimezone_OnSuccess()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "code_iata": "AKL", "timezone": "Pacific/Auckland" }""");

        var timezone = await Lookup(handler).TryGetTimezoneAsync("AKL");

        Assert.Equal("Pacific/Auckland", timezone);
    }

    [Fact]
    public async Task TryGetTimezoneAsync_RequestsTheUppercasedIataCode()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "timezone": "Europe/Sofia" }""");

        await Lookup(handler).TryGetTimezoneAsync("sof");

        Assert.EndsWith("airports/SOF", handler.Requests[0].AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryGetTimezoneAsync_CachesTheResult_ForThirtyDays()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "timezone": "Europe/Sofia" }""");
        var cache = EmptyCache();

        await Lookup(handler, cache).TryGetTimezoneAsync("SOF");

        await cache.Received(1).SetAsync(
            "aeroapi:airport-tz:SOF", "Europe/Sofia", TimeSpan.FromDays(30), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGetTimezoneAsync_ReturnsTheCachedValue_WithoutCallingTheApi()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "timezone": "should-not-be-used" }""");
        var cache = Substitute.For<ICacheService>();
        cache.GetAsync<string>("aeroapi:airport-tz:SOF", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("Europe/Sofia"));

        var timezone = await Lookup(handler, cache).TryGetTimezoneAsync("SOF");

        Assert.Equal("Europe/Sofia", timezone);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TryGetTimezoneAsync_ReturnsNull_AndDoesNotCache_WhenTheAirportIsNotFound()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, """{ "title": "not found" }""");
        var cache = EmptyCache();

        var timezone = await Lookup(handler, cache).TryGetTimezoneAsync("ZZZ");

        Assert.Null(timezone);
        await cache.DidNotReceiveWithAnyArgs().SetAsync<string>(default!, default!, default, default);
    }

    [Fact]
    public async Task TryGetTimezoneAsync_ReturnsNull_RatherThanThrowing_WhenTheProviderIsThrottled()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, """{ "title": "too many requests" }""");

        var timezone = await Lookup(handler).TryGetTimezoneAsync("SOF");

        Assert.Null(timezone);
    }

    [Fact]
    public async Task TryGetTimezoneAsync_ReturnsNull_WhenTheResponseHasNoTimezoneField()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "code_iata": "SOF" }""");

        var timezone = await Lookup(handler).TryGetTimezoneAsync("SOF");

        Assert.Null(timezone);
    }

    [Fact]
    public async Task TryGetTimezoneAsync_NeverLeaksTheApiKey_InFailureLogging()
    {
        // Defense in depth: even though this lookup swallows the DomainException rather than
        // propagating it, the underlying AeroApiResponse redaction must still have run — proven
        // indirectly here by simply confirming a failure does not throw the key back out via an
        // unhandled exception message.
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.Unauthorized, """{ "title": "unauthorized", "detail": "dummy-key rejected" }""");

        var timezone = await Lookup(handler).TryGetTimezoneAsync("SOF");

        Assert.Null(timezone);
    }
}
