using System.Globalization;
using System.Net;
using System.Text;
using FlightAssistant.Infrastructure.ExternalServices.FlightSearch;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.FlightSearch;

/// <summary>
/// Tests the AeroDataBox flight-search provider. The provider must never issue a search window
/// longer than 12 hours (the endpoint rejects longer windows with HTTP 400), so a full service
/// day is split into two consecutive windows and the results are merged + deduped.
/// </summary>
public class AeroDataBoxFlightSearchProviderTests
{
    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _bodyForUri;
        public List<Uri> Requests { get; } = [];

        public CapturingHttpMessageHandler(Func<string, string> bodyForUri) => _bodyForUri = bodyForUri;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodyForUri(request.RequestUri!.ToString()), Encoding.UTF8, "application/json")
            });
        }
    }

    private static string Departure(string number, string arrIata, string scheduledUtc) => $$"""
        {
          "number": "{{number}}",
          "airline": { "iata": "{{number[..2]}}" },
          "departure": { "scheduledTimeUtc": "{{scheduledUtc}}" },
          "arrival": { "airport": { "iata": "{{arrIata}}" }, "scheduledTimeUtc": "{{scheduledUtc}}" },
          "aircraft": { "model": "A320" }
        }
        """;

    private static (DateTime From, DateTime To) ParseWindow(Uri uri)
    {
        // .../iata/{iata}/{from}/{to}?...  — from/to are the path segments after the iata code.
        var path = uri.AbsolutePath;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var to = DateTime.ParseExact(segments[^1], "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        var from = DateTime.ParseExact(segments[^2], "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        return (from, to);
    }

    private static AeroDataBoxFlightSearchProvider Provider(CapturingHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://stub/") },
            NullLogger<AeroDataBoxFlightSearchProvider>.Instance);

    [Fact]
    public async Task SearchAsync_NeverRequestsWindowLongerThan12Hours()
    {
        var handler = new CapturingHttpMessageHandler(_ => """{ "departures": [] }""");
        var provider = Provider(handler);

        await provider.SearchAsync("LHR", "JFK", new DateOnly(2025, 6, 15));

        Assert.NotEmpty(handler.Requests);
        foreach (var window in handler.Requests.Select(ParseWindow))
        {
            Assert.True(window.To > window.From, "End of window must be after start.");
            Assert.True(window.To - window.From <= TimeSpan.FromHours(12),
                $"Window {window.From:o}..{window.To:o} exceeds the 12h AeroDataBox limit.");
        }
    }

    [Fact]
    public async Task SearchAsync_MergesResultsFromBothWindows()
    {
        // Morning window returns BA1 (08:00), afternoon window returns BA2 (15:00).
        var handler = new CapturingHttpMessageHandler(uri =>
            ParseWindow(new Uri(uri)).From.Hour < 12
                ? $$"""{ "departures": [ {{Departure("BA1", "JFK", "2025-06-15T08:00:00Z")}} ] }"""
                : $$"""{ "departures": [ {{Departure("BA2", "JFK", "2025-06-15T15:00:00Z")}} ] }""");
        var provider = Provider(handler);

        var results = await provider.SearchAsync("LHR", "JFK", new DateOnly(2025, 6, 15));

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.FlightNumber == "BA1");
        Assert.Contains(results, r => r.FlightNumber == "BA2");
    }

    [Fact]
    public async Task SearchAsync_DedupesFlightAppearingInBothWindows()
    {
        // Same flight (same number + scheduled time) returned by both windows.
        var handler = new CapturingHttpMessageHandler(_ =>
            $$"""{ "departures": [ {{Departure("BA1", "JFK", "2025-06-15T12:00:00Z")}} ] }""");
        var provider = Provider(handler);

        var results = await provider.SearchAsync("LHR", "JFK", new DateOnly(2025, 6, 15));

        Assert.Single(results);
    }

    [Fact]
    public async Task SearchAsync_FiltersOutNonMatchingArrivalAirport()
    {
        var handler = new CapturingHttpMessageHandler(_ =>
            $$"""{ "departures": [ {{Departure("BA9", "CDG", "2025-06-15T09:00:00Z")}} ] }""");
        var provider = Provider(handler);

        var results = await provider.SearchAsync("LHR", "JFK", new DateOnly(2025, 6, 15));

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyOnErrorStatus_WithoutThrowing()
    {
        var handler = new ErrorHandler(HttpStatusCode.BadRequest, "window too long");
        var provider = new AeroDataBoxFlightSearchProvider(
            new HttpClient(handler) { BaseAddress = new Uri("http://stub/") },
            NullLogger<AeroDataBoxFlightSearchProvider>.Instance);

        var results = await provider.SearchAsync("LHR", "JFK", new DateOnly(2025, 6, 15));

        Assert.Empty(results);
    }

    private sealed class ErrorHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public ErrorHandler(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }
}
