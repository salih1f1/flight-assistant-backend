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

    public void Dispose() => _connection.Dispose();
}
