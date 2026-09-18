using System.Net;
using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Infrastructure.DataSources;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FlightAssistant.Infrastructure.Tests.DataSources
{
    public class DataSourceObservingHandlerTests
    {
        private const string SourceKey = "aeroapi";
        private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        /// <summary>Keeps the last observation so a test can assert on what the handler recorded.</summary>
        private sealed class RecordingObserver : IDataSourceObserver
        {
            public (string Key, DataSourceOutcome Outcome, DateTimeOffset At, string? Detail)? Last { get; private set; }

            public void Record(string key, DataSourceOutcome outcome, DateTimeOffset at, string? detail = null) =>
                Last = (key, outcome, at, detail);
        }

        /// <summary>The observer misbehaving: recording must never be able to fail the call.</summary>
        private sealed class ThrowingObserver : IDataSourceObserver
        {
            public void Record(string key, DataSourceOutcome outcome, DateTimeOffset at, string? detail = null) =>
                throw new InvalidOperationException("observer is broken");
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(_respond(request));
        }

        private static HttpClient BuildClient(IDataSourceObserver observer, Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            var handler = new DataSourceObservingHandler(observer, new FakeTimeProvider(Now))
            {
                SourceKey = SourceKey,
                InnerHandler = new StubHandler(respond),
            };

            return new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        }

        [Theory]
        [InlineData(200, DataSourceOutcome.Ok)]
        [InlineData(204, DataSourceOutcome.Ok)]
        [InlineData(401, DataSourceOutcome.Unauthorized)]
        [InlineData(403, DataSourceOutcome.Unauthorized)]
        [InlineData(429, DataSourceOutcome.RateLimited)]
        [InlineData(500, DataSourceOutcome.Failed)]
        [InlineData(503, DataSourceOutcome.Failed)]
        public async Task MapsAStatusCodeToAnOutcome(int statusCode, DataSourceOutcome expected)
        {
            var observer = new RecordingObserver();
            var client = BuildClient(observer, _ => new HttpResponseMessage((HttpStatusCode)statusCode));

            await client.GetAsync("/anything");

            Assert.Equal(expected, observer.Last!.Value.Outcome);
        }

        [Fact]
        public async Task ASuccessRecordsNoDetail()
        {
            var observer = new RecordingObserver();
            var client = BuildClient(observer, _ => new HttpResponseMessage(HttpStatusCode.OK));

            await client.GetAsync("/anything");

            Assert.Null(observer.Last!.Value.Detail);
        }

        [Fact]
        public async Task AFailingStatusCodeIsNamedInTheDetail()
        {
            var observer = new RecordingObserver();
            var client = BuildClient(observer, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            await client.GetAsync("/anything");

            Assert.Equal($"{SourceKey} returned HTTP 503", observer.Last!.Value.Detail);
        }

        [Fact]
        public async Task ATransportFailure_IsRecordedAsFailed_AndStillThrows()
        {
            var observer = new RecordingObserver();
            var client = BuildClient(observer, _ => throw new HttpRequestException("connection refused"));

            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/anything"));

            Assert.Equal(DataSourceOutcome.Failed, observer.Last!.Value.Outcome);
        }

        // The global constraint: an upstream message can name an internal address.
        [Fact]
        public async Task ATransportFailure_DoesNotLeakTheExceptionMessage()
        {
            var observer = new RecordingObserver();
            var client = BuildClient(observer, _ => throw new HttpRequestException("connect failed to 10.0.0.7:5432"));

            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/anything"));

            Assert.DoesNotContain("10.0.0.7", observer.Last!.Value.Detail ?? "");
        }

        [Fact]
        public async Task AnObserverThatThrows_DoesNotBreakTheCall()
        {
            var client = BuildClient(new ThrowingObserver(), _ => new HttpResponseMessage(HttpStatusCode.OK));

            var response = await client.GetAsync("/anything");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
