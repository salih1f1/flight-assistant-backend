using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Testcontainers.PostgreSql;

namespace FlightAssistant.Infrastructure.Tests.TestSupport;

/// <summary>
/// One real PostgreSQL container — <c>postgres:16-alpine</c>, matching <c>docker-compose.yml</c>
/// and production — shared by every test class in the <see cref="PostgresCollection"/> xUnit
/// collection (see #295).
///
/// <para>
/// <b>Why a container per collection, not per class.</b> Container startup dominates test
/// runtime. xUnit creates exactly one instance of an <see cref="ICollectionFixture{TFixture}"/>
/// per collection and shares it across every test class carrying that collection's
/// <c>[Collection]</c> attribute, so the container is started once for the whole run rather than
/// once per class.
/// </para>
///
/// <para>
/// <b>Why <see cref="Database.MigrateAsync"/>, not <c>EnsureCreated</c>.</b> <c>EnsureCreated</c>
/// builds a schema straight from the current model and never touches the migrations table, so it
/// would not catch a broken migration, a missing <c>HasData</c> seed, or drift between the
/// compiled model and the migration history — several of which this repo has hit before.
/// Migrating is exactly what production does on deploy.
/// </para>
///
/// <para>
/// <b>Isolation strategy — reset by truncation, not a container or schema per test.</b>
/// <see cref="ResetAsync"/> truncates every application table except ones populated purely by
/// <c>HasData</c> seeding (<c>flight_statuses</c> — a fixed reference vocabulary nothing at
/// runtime ever writes to, so there is nothing to reset and truncating it would just force every
/// test that touches it to re-seed by hand). Table names and the seed exclusion are read from the
/// live EF model rather than hand-maintained, so a newly added entity is picked up automatically
/// without editing this fixture.
/// </para>
///
/// <para>
/// A test class opts in by carrying <c>[Collection(PostgresCollection.Name)]</c> and calling
/// <see cref="ResetAsync"/> from its own <c>IAsyncLifetime.InitializeAsync</c> (see
/// <see cref="PostgresTestBase"/> for the base class that does this) — xUnit runs test classes
/// *within* one collection sequentially (parallelization happens only *across* collections), so a
/// reset-before-each-test is race-free without any locking of our own.
/// </para>
///
/// <para>
/// <b>What this does and does not protect against.</b> It protects against leftover rows from one
/// test being visible to, or colliding with, another test that shares this collection. It does
/// <i>not</i> protect against a test class that carries the collection attribute but forgets to
/// call <see cref="ResetAsync"/> (state then leaks forward from whatever ran immediately before
/// it), and it does not protect against genuine concurrent access — it relies on xUnit's default
/// same-collection-is-sequential behaviour, not on database-level locking, so it would silently
/// stop being safe if collection parallelization were ever turned on for this collection.
/// </para>
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>Set once <see cref="InitializeAsync"/> has started the container.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>A fresh <see cref="FlightAssistantDbContext"/> bound to the running container.
    /// Callers own disposal.</summary>
    public FlightAssistantDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new FlightAssistantDbContext(options);
    }

    /// <summary>
    /// Truncates every application table except ones seeded via <c>HasData</c>, resetting identity
    /// sequences (<c>RESTART IDENTITY</c>) so tests that assert on generated ids stay predictable.
    /// <c>CASCADE</c> is required because application tables reference each other by foreign key;
    /// it only reaches *dependents* of the tables listed, so the excluded, seed-backed
    /// <c>flight_statuses</c> table (a referenced parent, not a dependent) is never touched by it.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var db = CreateDbContext();

        // HasData seed rows are stripped from the runtime model (see FlightStatusConfigurationTests
        // for why) — GetSeedData() only answers on the design-time model.
        var seedBackedTables = db.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Where(e => e.GetSeedData().Any())
            .Select(e => e.GetTableName())
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        var tableNames = db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(name => name is not null && !seedBackedTables.Contains(name))
            .Distinct()
            .ToList();

        if (tableNames.Count == 0) return;

        // Table names come from EF's own metadata, never external input, but EF1002 flags any
        // interpolated-string *literal* passed to ExecuteSqlRawAsync regardless of source — so the
        // SQL is built as a plain string first, which the analyzer has nothing to flag on.
        var quoted = string.Join(", ", tableNames.Select(n => "\"" + n + "\""));
        var sql = "TRUNCATE TABLE " + quoted + " RESTART IDENTITY CASCADE;";
        await db.Database.ExecuteSqlRawAsync(sql);
    }
}
