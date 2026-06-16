using System.Net;
using System.Text;
using FlightAssistant.Infrastructure.ExternalServices.FlightStatus;
using Microsoft.Extensions.Configuration;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.FlightStatus;

/// <summary>
/// Tests that both flight-status providers parse API timestamps as UTC, regardless of whether
/// the incoming string carries an explicit timezone designator. An offset-less string such as
/// "2025-06-15T10:30:00" must be interpreted as UTC (not shifted by the host's local offset),
/// and an explicit-UTC string such as "2025-06-15T10:30:00Z" must yield the same instant.
///
/// Each provider is driven through its real <c>GetStatusAsync</c> with a stub
/// <see cref="HttpMessageHandler"/> returning a canned JSON payload, so the parsing path
/// (<c>MapToResult</c> / <c>TryGetDateTime</c>) is exercised end-to-end.
/// </summary>
public class FlightStatusProviderParsingTests
{
    private static readonly DateTime ExpectedUtc = new(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StubHttpMessageHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }

    private static HttpClient StubClient(string body) =>
        new(new StubHttpMessageHandler(body)) { BaseAddress = new Uri("http://stub/") };

    [Theory]
    [InlineData("2025-06-15T10:30:00")]  // offset-less: must be assumed UTC, not shifted to UTC from local
    [InlineData("2025-06-15T10:30:00Z")] // explicit UTC: must yield the same instant
    public async Task AeroDataBox_ParsesActualDepartureAsUtc(string timestamp)
    {
        var body = $$"""
        [
          {
            "number": "BA123",
            "status": "Departed",
            "departure": { "actualTimeUtc": "{{timestamp}}" },
            "arrival": {},
            "aircraft": { "model": "A320" }
          }
        ]
        """;
        var provider = new AeroDataBoxFlightStatusProvider(StubClient(body));

        var result = await provider.GetStatusAsync("BA123", new DateOnly(2025, 6, 15));

        Assert.NotNull(result);
        Assert.NotNull(result!.ActualDepartureUtc);
        Assert.Equal(ExpectedUtc, result.ActualDepartureUtc);
        Assert.Equal(DateTimeKind.Utc, result.ActualDepartureUtc!.Value.Kind);
        Assert.Equal(ExpectedUtc, result.EffectiveAtUtc);
        Assert.Equal(DateTimeKind.Utc, result.EffectiveAtUtc.Kind);
    }

    [Theory]
    [InlineData("2025-06-15T10:30:00")]  // offset-less: must be assumed UTC, not shifted to UTC from local
    [InlineData("2025-06-15T10:30:00Z")] // explicit UTC: must yield the same instant
    public async Task AviationStack_ParsesActualDepartureAsUtc(string timestamp)
    {
        var body = $$"""
        {
          "data": [
            {
              "flight_status": "active",
              "departure": { "actual": "{{timestamp}}" },
              "arrival": {},
              "flight": { "iata": "BA123" }
            }
          ]
        }
        """;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AviationStack:ApiKey"] = "dummy" })
            .Build();
        var provider = new AviationStackFlightStatusProvider(StubClient(body), config);

        var result = await provider.GetStatusAsync("BA123", new DateOnly(2025, 6, 15));

        Assert.NotNull(result);
        Assert.NotNull(result!.ActualDepartureUtc);
        Assert.Equal(ExpectedUtc, result.ActualDepartureUtc);
        Assert.Equal(DateTimeKind.Utc, result.ActualDepartureUtc!.Value.Kind);
        Assert.Equal(ExpectedUtc, result.EffectiveAtUtc);
        Assert.Equal(DateTimeKind.Utc, result.EffectiveAtUtc.Kind);
    }
}
