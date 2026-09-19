using System.Net;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroDataBox
{
    public class AeroDataBoxQuotaHandlerTests
    {
        private static (HttpClient Client, AeroDataBoxQuotaHolder Holder) Build(
            HttpResponseMessage response, FakeTimeProvider time)
        {
            var holder = new AeroDataBoxQuotaHolder();
            var handler = new AeroDataBoxQuotaHandler(holder, time)
            {
                InnerHandler = new StubHandler(response),
            };
            return (new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, holder);
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly HttpResponseMessage _response;
            public StubHandler(HttpResponseMessage response) => _response = response;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
                Task.FromResult(_response);
        }

        [Fact]
        public async Task SendAsync_RecordsTheQuotaHeaders()
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("x-ratelimit-requests-remaining", "1487");
            response.Headers.Add("x-ratelimit-requests-limit", "1505");
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

            var (client, holder) = Build(response, time);
            await client.GetAsync("anything");

            var quota = holder.AeroDataBox;
            Assert.NotNull(quota);
            Assert.Equal(1487, quota!.Remaining);
            Assert.Equal(1505, quota.Limit);
            Assert.Equal(time.GetUtcNow(), quota.ObservedUtc);
        }

        [Fact]
        public async Task AeroDataBox_BeforeAnyCall_IsNullNotZero()
        {
            // "Cannot tell" is a weaker claim than "nothing left" — the rule
            // AdminHealthMigrationsDto.Available already encodes. Reporting 0 here would show an
            // exhausted quota on every fresh deploy.
            var holder = new AeroDataBoxQuotaHolder();
            Assert.Null(holder.AeroDataBox);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task SendAsync_MissingHeaders_LeavesTheLastKnownValueAlone()
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
            var holder = new AeroDataBoxQuotaHolder();
            holder.Record(1487, 1505, time.GetUtcNow());

            var response = new HttpResponseMessage(HttpStatusCode.OK);
            var handler = new AeroDataBoxQuotaHandler(holder, time) { InnerHandler = new StubHandler(response) };
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

            await client.GetAsync("anything");

            Assert.Equal(1487, holder.AeroDataBox!.Remaining);
        }

        [Fact]
        public async Task SendAsync_TransportFailure_RethrowsUnchanged()
        {
            // Diagnostics must never alter or break the thing being diagnosed — the same rule
            // DataSourceObservingHandler states.
            var time = new FakeTimeProvider();
            var holder = new AeroDataBoxQuotaHolder();
            var handler = new AeroDataBoxQuotaHandler(holder, time)
            {
                InnerHandler = new ThrowingHandler(),
            };
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("anything"));
            Assert.Null(holder.AeroDataBox);
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
                throw new HttpRequestException("down");
        }
    }
}
