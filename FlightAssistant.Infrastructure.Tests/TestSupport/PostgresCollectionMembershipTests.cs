using System.Reflection;

namespace FlightAssistant.Infrastructure.Tests.TestSupport;

/// <summary>
/// Guards issue #310: joining <see cref="PostgresCollection"/> is opt-in by convention — a test
/// class can carry <c>[Collection(PostgresCollection.Name)]</c>, take the shared container, and
/// never call <see cref="PostgresContainerFixture.ResetAsync"/>, silently inheriting whatever rows
/// the previous test in the collection left behind (xUnit runs same-collection tests sequentially,
/// so nothing else would ever surface the leak as a flaky failure — it would just quietly poison
/// assertions that assume an empty table). <see cref="PostgresTestBase"/> is what calls
/// <c>ResetAsync</c> before every test, so every collection member deriving from it is enough to
/// close the gap.
///
/// <para>
/// This class deliberately does <b>not</b> carry <c>[Collection(PostgresCollection.Name)]</c>
/// itself — it needs no container, and tagging it would make it check itself.
/// </para>
/// </summary>
public class PostgresCollectionMembershipTests
{
    [Fact]
    public void EveryPostgresCollectionMember_DerivesFromPostgresTestBase()
    {
        var offenders = typeof(PostgresCollection).Assembly.GetTypes()
            .Where(t => GetCollectionAttributeName(t) == PostgresCollection.Name)
            .Where(t => !typeof(PostgresTestBase).IsAssignableFrom(t))
            .ToList();

        Assert.True(offenders.Count == 0, BuildFailureMessage(offenders));
    }

    // Xunit.CollectionAttribute exposes its constructor argument only through
    // CustomAttributeData, not as a reflectable public "Name" property (verified against the
    // xunit 2.9.3 package this project references) — so the collection name is read from the
    // attribute's recorded constructor argument rather than a property that doesn't exist.
    private static string? GetCollectionAttributeName(Type type) =>
        type.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute))
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    private static string BuildFailureMessage(IReadOnlyCollection<Type> offenders) =>
        "The following test classes carry [Collection(PostgresCollection.Name)] but do not " +
        "derive from PostgresTestBase, so they take the shared Postgres container without ever " +
        "resetting it — they silently inherit rows left behind by whatever ran immediately " +
        "before them in the collection (see #310). Fix: make the class extend PostgresTestBase " +
        "and pass the injected PostgresContainerFixture to its constructor, e.g. " +
        "\"public MyTests(PostgresContainerFixture fixture) : base(fixture) { }\". Offending " +
        $"type(s): {string.Join(", ", offenders.Select(t => t.FullName))}.";
}
