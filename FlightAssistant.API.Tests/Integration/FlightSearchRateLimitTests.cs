using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// The flight-search rate limit (#371). Every request here is a search the validator rejects (a date
/// with no route and no flight number): it returns 400 without reaching the search provider, which in
/// this host is the real AeroAPI one, but the rate limiter runs before the endpoint, so it still spends a
/// permit. Each test uses its own client addresses and travelers, because the limiter's state is shared
/// with every other test in the Integration collection.
/// </summary>
[Collection("Integration")]
public class FlightSearchRateLimitTests(FlightAssistantWebApplicationFactory factory)
{
    private int FlightSearchPermitLimit() =>
        factory.Services.GetRequiredService<IConfiguration>().GetValue("RateLimiting:FlightSearch:PermitLimit", 8);

    private static HttpRequestMessage RejectedSearch(string path, string? forwardedFor = null)
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var request = new HttpRequestMessage(HttpMethod.Get, $"{path}?date={date}");
        if (forwardedFor is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return request;
    }

    /// <summary>A documentation-range IPv6 address, unique enough that no other test shares its bucket.</summary>
    private static string UniqueClientIp() =>
        $"2001:db8::{Random.Shared.Next(1, 0xFFFF):x}:{Random.Shared.Next(1, 0xFFFF):x}";

    private async Task SpendAllPermitsAsync(HttpClient client, string path, string? forwardedFor = null)
    {
        for (var i = 0; i < FlightSearchPermitLimit(); i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(RejectedSearch(path, forwardedFor))).StatusCode);
    }

    // ── Forwarded headers (#371) ──

    [Fact]
    public void ForwardedHeaders_TrustOnlyTheEntryTheFrontEndAppends()
    {
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Empty(options.KnownProxies);
    }

    [Fact]
    public async Task AnonymousSearch_DifferentClientAddresses_DoNotShareABucket()
    {
        var client = factory.CreateClient();
        var addressA = UniqueClientIp();
        var addressB = UniqueClientIp();

        await SpendAllPermitsAsync(client, "flights/search", addressA);
        var rejected = await client.SendAsync(RejectedSearch("flights/search", addressA));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(RejectedSearch("flights/search", addressB))).StatusCode);
    }

    [Fact]
    public async Task AnonymousSearch_AClientSuppliedForwardedEntry_DoesNotChooseTheBucket()
    {
        // App Service's front end appends the real client address to whatever X-Forwarded-For the client
        // sent. Only that rightmost entry may count, or a client could pick a fresh bucket per request.
        var client = factory.CreateClient();
        var realAddress = UniqueClientIp();

        await SpendAllPermitsAsync(client, "flights/search", $"{UniqueClientIp()}, {realAddress}");

        var spoofed = await client.SendAsync(RejectedSearch("flights/search", $"{UniqueClientIp()}, {realAddress}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, spoofed.StatusCode);
    }
}
