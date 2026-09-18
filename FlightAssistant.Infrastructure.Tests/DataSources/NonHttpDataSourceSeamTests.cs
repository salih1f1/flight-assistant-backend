using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.DataSources;
using FlightAssistant.Infrastructure.HealthChecks;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FlightAssistant.Infrastructure.Tests.DataSources
{
    /// <summary>
    /// The two sources reached over something other than HTTP, so
    /// <see cref="DataSourceObservingHandler"/> cannot see them: the database and Redis. Each
    /// records an observation without changing what the caller gets back — a health check still
    /// answers Healthy/Unhealthy, and a cache failure still propagates.
    /// </summary>
    public class NonHttpDataSourceSeamTests
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        private sealed class RecordingObserver : IDataSourceObserver
        {
            public (string Key, DataSourceOutcome Outcome, string? Detail)? Last { get; private set; }

            public void Record(string key, DataSourceOutcome outcome, DateTimeOffset at, string? detail = null) =>
                Last = (key, outcome, detail);
        }

        private static FlightAssistantDbContext InMemoryContext() =>
            new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        /// <summary>A context whose provider cannot reach anything: connecting throws.</summary>
        private static FlightAssistantDbContext UnreachableContext() =>
            new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1")
                .Options);

        [Fact]
        public async Task DatabaseHealthCheck_RecordsOk_WhenTheDatabaseAnswers()
        {
            var observer = new RecordingObserver();
            await using var db = InMemoryContext();

            var result = await new DatabaseHealthCheck(db, observer, new FakeTimeProvider(Now))
                .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

            Assert.Equal(HealthStatus.Healthy, result.Status);
            Assert.Equal(DataSourceKeys.Postgres, observer.Last!.Value.Key);
            Assert.Equal(DataSourceOutcome.Ok, observer.Last!.Value.Outcome);
        }

        [Fact]
        public async Task DatabaseHealthCheck_RecordsFailed_AndStillAnswersUnhealthy()
        {
            var observer = new RecordingObserver();
            await using var db = UnreachableContext();

            var result = await new DatabaseHealthCheck(db, observer, new FakeTimeProvider(Now))
                .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(DataSourceOutcome.Failed, observer.Last!.Value.Outcome);
        }

        /// <summary>The global rule: a stored detail never carries an upstream exception's message.</summary>
        [Fact]
        public async Task DatabaseHealthCheck_DoesNotLeakTheExceptionMessage()
        {
            var observer = new RecordingObserver();
            await using var db = UnreachableContext();

            await new DatabaseHealthCheck(db, observer, new FakeTimeProvider(Now))
                .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

            Assert.DoesNotContain("127.0.0.1", observer.Last!.Value.Detail ?? "");
        }

        private sealed class ThrowingCache : IDistributedCache
        {
            public byte[]? Get(string key) => throw new InvalidOperationException("redis is down");
            public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("redis is down at 10.0.0.9:6379");
            public void Refresh(string key) { }
            public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
            public void Remove(string key) { }
            public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
            public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { }
            public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new InvalidOperationException("redis is down");
        }

        private sealed class EmptyCache : IDistributedCache
        {
            public byte[]? Get(string key) => null;
            public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
            public void Refresh(string key) { }
            public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
            public void Remove(string key) { }
            public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
            public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { }
            public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Task.CompletedTask;
        }

        [Fact]
        public async Task RedisCacheService_RecordsOk_OnACompletedRead()
        {
            var observer = new RecordingObserver();
            var cache = new RedisCacheService(new EmptyCache(), observer, new FakeTimeProvider(Now));

            await cache.GetAsync<string>("any-key", CancellationToken.None);

            Assert.Equal(DataSourceKeys.Redis, observer.Last!.Value.Key);
            Assert.Equal(DataSourceOutcome.Ok, observer.Last!.Value.Outcome);
        }

        [Fact]
        public async Task RedisCacheService_RecordsFailed_AndStillThrows()
        {
            var observer = new RecordingObserver();
            var cache = new RedisCacheService(new ThrowingCache(), observer, new FakeTimeProvider(Now));

            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync<string>("any-key", CancellationToken.None));

            Assert.Equal(DataSourceOutcome.Failed, observer.Last!.Value.Outcome);
            Assert.DoesNotContain("10.0.0.9", observer.Last!.Value.Detail ?? "");
        }

        [Fact]
        public async Task RedisCacheService_RecordsFailed_OnAWriteThatThrows()
        {
            var observer = new RecordingObserver();
            var cache = new RedisCacheService(new ThrowingCache(), observer, new FakeTimeProvider(Now));

            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.SetAsync("any-key", "value", null, CancellationToken.None));

            Assert.Equal(DataSourceOutcome.Failed, observer.Last!.Value.Outcome);
        }
    }
}
