using System.Net;
using System.Text;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Infrastructure.ExternalServices.AviationStack;
using FlightAssistant.Infrastructure.ExternalServices.FlightSearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.FlightSearch;

/// <summary>
/// AviationStack splits its data across two endpoints with no overlap, and a future date on the
/// real-time endpoint returns an empty set rather than an error — indistinguishable from "no such
/// route". These tests pin which provider each date reaches.
/// </summary>
public class DateRoutingFlightSearchProviderTests
{
    /// <summary>Records which endpoint was hit, so routing can be asserted by URL path.</summary>
    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static (DateRoutingFlightSearchProvider Provider, RecordingHandler Live, RecordingHandler Schedule)
        Build(DateTimeOffset utcNow)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AviationStack:ApiKey"] = "dummy" })
            .Build();

        var liveHandler = new RecordingHandler("""{ "data": [] }""");
        var scheduleHandler = new RecordingHandler("""{ "data": [] }""");

        var timezones = new AviationStackAirportTimezoneLookup(
            new HttpClient(new RecordingHandler("""{ "data": [] }""")) { BaseAddress = new Uri("http://stub/") },
            Substitute.For<ICacheService>(), config, NullLogger<AviationStackAirportTimezoneLookup>.Instance);

        var live = new AviationStackLiveSearchProvider(
            new HttpClient(liveHandler) { BaseAddress = new Uri("http://stub/") },
            timezones, config, NullLogger<AviationStackLiveSearchProvider>.Instance);

        var resolver = new AviationStackRouteResolver(
            new HttpClient(new RecordingHandler("""{ "data": [] }""")) { BaseAddress = new Uri("http://stub/") },
            config, NullLogger<AviationStackRouteResolver>.Instance);

        var schedule = new AviationStackScheduleSearchProvider(
            new HttpClient(scheduleHandler) { BaseAddress = new Uri("http://stub/") },
            resolver,
            new OperatingCarrierResolver(
                new HttpClient(new RecordingHandler("""{ "data": [] }""")) { BaseAddress = new Uri("http://stub/") },
                config, NullLogger<OperatingCarrierResolver>.Instance),
            timezones, config,
            NullLogger<AviationStackScheduleSearchProvider>.Instance);

        var provider = new DateRoutingFlightSearchProvider(
            live, schedule,
            NullLogger<DateRoutingFlightSearchProvider>.Instance,
            new FakeTimeProvider(utcNow));

        return (provider, liveHandler, scheduleHandler);
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2026, 8, 29)]  // today
    [InlineData(2026, 8, 28)]  // yesterday
    [InlineData(2026, 2, 20)]  // months back
    public async Task PastAndPresentDates_GoToTheRealTimeEndpoint(int y, int m, int d)
    {
        var (provider, live, schedule) = Build(Now);

        await provider.SearchAsync(new FlightSearchCriteria("SOF", "IST", new DateOnly(y, m, d), null));

        Assert.Contains("flights", live.Paths.Single());
        Assert.Empty(schedule.Paths);
    }

    [Theory]
    [InlineData(2026, 8, 30)]   // tomorrow — the first date with no real-time data
    [InlineData(2026, 10, 10)]
    public async Task FutureDates_GoToTheSchedulesEndpoint(int y, int m, int d)
    {
        var (provider, live, schedule) = Build(Now);

        await provider.SearchAsync(new FlightSearchCriteria("SOF", "IST", new DateOnly(y, m, d), null));

        Assert.Contains("flightsFuture", schedule.Paths.Single());
        Assert.Empty(live.Paths);
    }

    [Fact]
    public async Task BoundaryIsUtcToday_NotLocalMidnight()
    {
        // Late in the UTC day, "today" is still the real-time endpoint's territory.
        var (provider, live, schedule) = Build(new DateTimeOffset(2026, 8, 29, 23, 45, 0, TimeSpan.Zero));

        await provider.SearchAsync(new FlightSearchCriteria("SOF", "IST", new DateOnly(2026, 8, 29), null));

        Assert.Single(live.Paths);
        Assert.Empty(schedule.Paths);
    }
}
