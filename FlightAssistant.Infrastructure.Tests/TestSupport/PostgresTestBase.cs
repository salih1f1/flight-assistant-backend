using FlightAssistant.Infrastructure.Persistance;

namespace FlightAssistant.Infrastructure.Tests.TestSupport;

/// <summary>
/// Convenience base for a test class that needs real PostgreSQL behaviour. Resets the shared
/// container's state (see <see cref="PostgresContainerFixture.ResetAsync"/>) before every test —
/// xUnit constructs a fresh instance of the test class per test method, so
/// <see cref="IAsyncLifetime.InitializeAsync"/> here runs immediately before each one.
///
/// <para>
/// A deriving class must still carry <c>[Collection(PostgresCollection.Name)]</c> itself —
/// xUnit does not honor a <c>[Collection]</c> attribute inherited from a base class — and accept
/// <see cref="PostgresContainerFixture"/> in its constructor to pass to this base.
/// </para>
/// </summary>
public abstract class PostgresTestBase : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;

    protected PostgresTestBase(PostgresContainerFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A fresh <see cref="FlightAssistantDbContext"/> bound to the shared container.
    /// Callers own disposal.</summary>
    protected FlightAssistantDbContext CreateDbContext() => _fixture.CreateDbContext();
}
