namespace FlightAssistant.Infrastructure.Tests.TestSupport;

/// <summary>
/// xUnit collection binding every Postgres-backed test class to one shared
/// <see cref="PostgresContainerFixture"/> instance — see that class for what "shared" buys and
/// costs. Add <c>[Collection(Name)]</c> to a test class to join it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresContainerFixture>
{
    public const string Name = "Postgres";
}
