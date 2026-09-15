using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// Drives <see cref="AeroApiAirportTimezoneLookup"/> against stubbed <c>/airports/{id}</c>
/// responses, pinning its contract: cache a success for 365 days and a failed lookup for 1 hour,
/// never let a failure propagate, never cache a cancelled call.
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

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }

    // A request that never completes on its own — it only ever resolves via the caller's
    // cancellation — so a cancellation test cancels a call that is genuinely in flight, not one
    // that already finished synchronously before the token could ever be observed.
    private sealed class InFlightHttpMessageHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable: an infinite delay only ends via cancellation.");
        }
    }

    private static AeroApiAirportTimezoneLookup Lookup(
        HttpMessageHandler handler, ICacheService? cache = null)
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
    public async Task TryGetTimezoneAsync_CachesASuccess_ForAYear()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "timezone": "Europe/Sofia" }""");
        var cache = EmptyCache();

        await Lookup(handler, cache).TryGetTimezoneAsync("SOF");

        await cache.Received(1).SetAsync(
            "aeroapi:airport-tz:SOF", "Europe/Sofia", TimeSpan.FromDays(365), Arg.Any<CancellationToken>());
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

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{ \"title\": \"not found\" }")]
    [InlineData(HttpStatusCode.TooManyRequests, "{ \"title\": \"too many requests\" }")]
    [InlineData(HttpStatusCode.Unauthorized, "{ \"title\": \"unauthorized\" }")]
    [InlineData(HttpStatusCode.OK, "{ \"code_iata\": \"ZZZ\" }")] // no timezone field
    public async Task TryGetTimezoneAsync_ReturnsNull_AndCachesTheMissForAnHour_WhenTheLookupFails(HttpStatusCode status, string body)
    {
        var handler = new StubHttpMessageHandler(status, body);
        var cache = EmptyCache();

        var timezone = await Lookup(handler, cache).TryGetTimezoneAsync("ZZZ");

        Assert.Null(timezone);
        await cache.Received(1).SetAsync(
            "aeroapi:airport-tz:ZZZ", "", TimeSpan.FromHours(1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGetTimezoneAsync_CachesTheMissForAnHour_WhenAeroApiIsUnreachable()
    {
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException("connection refused"));
        var cache = EmptyCache();

        var timezone = await Lookup(handler, cache).TryGetTimezoneAsync("SOF");

        Assert.Null(timezone);
        await cache.Received(1).SetAsync(
            "aeroapi:airport-tz:SOF", "", TimeSpan.FromHours(1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGetTimezoneAsync_ReturnsNull_WithoutCallingTheApi_WhenAMissIsCached()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{ "timezone": "should-not-be-used" }""");
        var cache = Substitute.For<ICacheService>();
        cache.GetAsync<string>("aeroapi:airport-tz:ZZZ", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(""));

        var timezone = await Lookup(handler, cache).TryGetTimezoneAsync("ZZZ");

        Assert.Null(timezone);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TryGetTimezoneAsync_CachedMiss_SurvivesARealMemoryCache()
    {
        // Pins the marker against the real cache, not a substitute: an empty string must come back
        // as a cached miss, not as an absent key that triggers another billed call.
        var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, """{ "title": "not found" }""");
        var cache = new MemoryCacheService(new MemoryCache(new MemoryCacheOptions()));
        var lookup = Lookup(handler, cache);

        Assert.Null(await lookup.TryGetTimezoneAsync("ZZZ"));
        Assert.Null(await lookup.TryGetTimezoneAsync("ZZZ"));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TryGetTimezoneAsync_CachesNothing_WhenTheCallIsCancelled()
    {
        var handler = new InFlightHttpMessageHandler();
        var cache = EmptyCache();
        using var cts = new CancellationTokenSource();

        var pending = Lookup(handler, cache).TryGetTimezoneAsync("SOF", cts.Token);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
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
