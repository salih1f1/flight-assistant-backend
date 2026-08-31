using FlightAssistant.API.HostedServices;
using FlightAssistant.Domain.Configuration.Entities;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.API.Tests.HostedServices;

/// <summary>
/// <see cref="AeroApiUsageRetentionService.PruneOnceAsync"/> exercised directly — not through the
/// infinite <see cref="Microsoft.Extensions.Hosting.BackgroundService.ExecuteAsync"/> loop — against
/// a real <see cref="FlightAssistantDbContext"/>, so the actual <c>ExecuteDeleteAsync</c> query is
/// proven rather than just the cutoff arithmetic. This deliberately uses a Sqlite in-memory database,
/// not the EF Core InMemory provider used elsewhere in this test suite: InMemory throws
/// <c>InvalidOperationException</c> for <c>ExecuteDelete</c>/<c>ExecuteUpdate</c> ("not supported by
/// the current database provider") because it is not a relational provider and cannot translate a
/// bulk statement — only a genuinely relational provider can prove this method works. A
/// <see cref="FakeTimeProvider"/> stands in for "now" so the retention cutoff is deterministic (see
/// issue #293).
/// </summary>
public sealed class AeroApiUsageRetentionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);

    // Kept open for the lifetime of the test: a Sqlite ":memory:" database lives only as long as its
    // connection, and every DbContext instance resolved from the container must share this one
    // connection (not just the same connection *string*) so the schema and rows persist across the
    // separate scopes PruneOnceAsync and the test's own setup/verification each open.
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private async Task<ServiceProvider> BuildProviderAsync(int retentionDays)
    {
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<FlightAssistantDbContext>(o => o.UseSqlite(_connection));
        services.Configure<AeroApiOptions>(o => o.UsageRetentionDays = retentionDays);
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return provider;
    }

    private static AeroApiCall NewCall(DateTime calledUtc) =>
        AeroApiCall.Record("/flights/{ident}", "GET", 200, 120, AeroApiCallOutcome.Success, "test", calledUtc);

    private static AeroApiUsageRetentionService NewService(ServiceProvider provider, FakeTimeProvider clock) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            clock,
            NullLogger<AeroApiUsageRetentionService>.Instance);

    [Fact]
    public async Task PruneOnceAsync_DeletesRowsOlderThanTheCutoff_AndKeepsNewerRows()
    {
        var clock = new FakeTimeProvider(Now);
        await using var provider = await BuildProviderAsync(retentionDays: 90);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            db.AeroApiCalls.AddRange(
                NewCall(Now.UtcDateTime.AddDays(-91)), // older than the 90-day cutoff -> deleted
                NewCall(Now.UtcDateTime.AddDays(-90).AddSeconds(-1)), // just past the cutoff -> deleted
                NewCall(Now.UtcDateTime.AddDays(-90)), // exactly at the cutoff -> kept
                NewCall(Now.UtcDateTime.AddDays(-1))); // well within retention -> kept
            await db.SaveChangesAsync();
        }

        var deleted = await NewService(provider, clock).PruneOnceAsync(CancellationToken.None);

        Assert.Equal(2, deleted);

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var remaining = await verifyDb.AeroApiCalls.ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, c => Assert.True(c.CalledUtc >= Now.UtcDateTime.AddDays(-90)));
    }

    [Fact]
    public async Task PruneOnceAsync_NoRowsOlderThanTheCutoff_DeletesNothing()
    {
        var clock = new FakeTimeProvider(Now);
        await using var provider = await BuildProviderAsync(retentionDays: 90);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            db.AeroApiCalls.Add(NewCall(Now.UtcDateTime.AddDays(-1)));
            await db.SaveChangesAsync();
        }

        var deleted = await NewService(provider, clock).PruneOnceAsync(CancellationToken.None);

        Assert.Equal(0, deleted);

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        Assert.Equal(1, await verifyDb.AeroApiCalls.CountAsync());
    }

    [Fact]
    public async Task PruneOnceAsync_UsesTheConfiguredRetentionDays_NotAHardcodedDefault()
    {
        // A row 10 days old would survive the 90-day default but not a 5-day retention — proves
        // UsageRetentionDays actually drives the cutoff rather than a hardcoded constant.
        var clock = new FakeTimeProvider(Now);
        await using var provider = await BuildProviderAsync(retentionDays: 5);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            db.AeroApiCalls.Add(NewCall(Now.UtcDateTime.AddDays(-10)));
            await db.SaveChangesAsync();
        }

        var deleted = await NewService(provider, clock).PruneOnceAsync(CancellationToken.None);

        Assert.Equal(1, deleted);
    }

    [Fact]
    public async Task PruneOnceAsync_AdvancingTheFakeClock_ChangesWhichRowsAreDue()
    {
        // Same row, same call, but the clock has moved on — the point of injecting TimeProvider
        // rather than reading DateTime.UtcNow directly: the cutoff is driven by "now", not baked in
        // at construction time.
        var clock = new FakeTimeProvider(Now);
        await using var provider = await BuildProviderAsync(retentionDays: 90);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            db.AeroApiCalls.Add(NewCall(Now.UtcDateTime.AddDays(-89)));
            await db.SaveChangesAsync();
        }

        var service = NewService(provider, clock);

        Assert.Equal(0, await service.PruneOnceAsync(CancellationToken.None)); // 89 days old, not yet due

        clock.Advance(TimeSpan.FromDays(2)); // now 91 days old

        Assert.Equal(1, await service.PruneOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PruneOnceAsync_ADatabaseFailure_IsLoggedAndSwallowed_NotThrown()
    {
        // The scope factory itself is broken (the container behind it is disposed), so
        // CreateScope() throws — a genuine failure wholly unrelated to the caller's token.
        var clock = new FakeTimeProvider(Now);
        var provider = await BuildProviderAsync(retentionDays: 90);
        var brokenScopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        await provider.DisposeAsync();

        var service = new AeroApiUsageRetentionService(
            brokenScopeFactory, clock, NullLogger<AeroApiUsageRetentionService>.Instance);

        var deleted = await service.PruneOnceAsync(CancellationToken.None);

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task PruneOnceAsync_ADatabaseFailure_IsLoggedAndSwallowed_EvenWhenTheCallersTokenIsAlreadyCancelled()
    {
        // Regression test for the review finding on this task: the exception filter used to be
        // gated on ct.IsCancellationRequested alone, not on whether the exception was actually an
        // OperationCanceledException caused by that cancellation. A genuine failure (here: the
        // scope factory's container is disposed — nothing to do with cancellation) racing with a
        // stoppingToken cancellation during host shutdown used to escape both this method's catch
        // AND ExecuteAsync's own OperationCanceledException catch, propagating out of ExecuteAsync
        // entirely — which the brief's "a failed prune ... must never crash the host" forbids
        // unconditionally, not just when the token happens to still be live.
        var clock = new FakeTimeProvider(Now);
        var provider = await BuildProviderAsync(retentionDays: 90);
        var brokenScopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        await provider.DisposeAsync();

        var service = new AeroApiUsageRetentionService(
            brokenScopeFactory, clock, NullLogger<AeroApiUsageRetentionService>.Instance);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync(); // simulates racing with a real host shutdown

        // Before the fix, this threw ObjectDisposedException instead of returning.
        var deleted = await service.PruneOnceAsync(cts.Token);

        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task ExecuteAsync_PrunesOnceOnStart_ThenWaitsAFullDayBeforePruningAgain()
    {
        // Drives the real BackgroundService loop (not just PruneOnceAsync in isolation) via the
        // public IHostedService.StartAsync/StopAsync, with every time-based decision — the initial
        // "now", and the daily wait — governed by a FakeTimeProvider rather than any real delay.
        var oneDay = TimeSpan.FromDays(1);
        var clock = new FakeTimeProvider(Now);
        await using var provider = await BuildProviderAsync(retentionDays: 90);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            db.AeroApiCalls.Add(NewCall(Now.UtcDateTime.AddDays(-91))); // due immediately
            await db.SaveChangesAsync();
        }

        var service = NewService(provider, clock);
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        await WaitUntilAsync(() => CountRowsAsync(provider, expected: 0));

        // The first pass already ran. Seed a second due row and confirm the loop is now parked on
        // its daily wait, NOT pruning again immediately.
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            db.AeroApiCalls.Add(NewCall(Now.UtcDateTime.AddDays(-91)));
            await db.SaveChangesAsync();
        }

        clock.Advance(oneDay - TimeSpan.FromSeconds(1));
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            // Correct regardless of exactly when ExecuteAsync's own (real, unavoidably-async DB
            // work-driven) continuation actually reaches and registers the daily Task.Delay: no
            // second pass can possibly have run yet either way, so the row is still here.
            Assert.Equal(1, await db.AeroApiCalls.CountAsync());
        }

        // A further, deliberately generous advance — not "+1 second" to land exactly on the
        // 1-day mark. The daily Task.Delay is registered against whatever fake "now" is current
        // AT THE MOMENT ExecuteAsync's own async continuation (real DB I/O, not under this test's
        // control) reaches that line, which may be slightly later than the advance above. A wide
        // safety margin makes this step immune to that gap while the negative assertion above
        // stays exact, since nothing before it could have advanced the loop past its wait either
        // way.
        clock.Advance(TimeSpan.FromDays(2));
        await WaitUntilAsync(() => CountRowsAsync(provider, expected: 0));

        await service.StopAsync(CancellationToken.None);
    }

    private static async Task<int> CountRowsAsync(ServiceProvider provider, int expected)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        return await db.AeroApiCalls.CountAsync() == expected ? expected : -1;
    }

    /// <summary>
    /// Polls a condition with short real waits, bounded to a couple of seconds, purely to
    /// synchronize the test with genuinely asynchronous background work (real Sqlite I/O on a
    /// separate continuation) that <see cref="FakeTimeProvider"/> has no way to fast-forward — it
    /// governs simulated business time (the daily wait), not wall-clock synchronization with the
    /// background service's own task scheduling. Every domain-time-dependent transition in this
    /// test is still driven exclusively by <c>clock.Advance</c>.
    /// </summary>
    private static async Task WaitUntilAsync(Func<Task<int>> pollExpectedOrMinusOne)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (await pollExpectedOrMinusOne() < 0)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Background prune pass did not complete in time.");
            await Task.Delay(10);
        }
    }

    public void Dispose() => _connection.Dispose();
}
