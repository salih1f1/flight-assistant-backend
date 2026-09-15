using FlightAssistant.Application.Administration;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Configurations.Verification;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// Guards (a) and (b) from the #315 Task 1 brief — the two <c>BrowsableTables</c> completeness
/// guards that need the compiled EF model, which lives in this project. They live here rather than
/// beside <c>SensitivePropertiesTests</c> in <c>FlightAssistant.Application.Tests</c> because that
/// project has no reference to <c>FlightAssistant.Infrastructure</c> — Application must not depend
/// on Infrastructure (see CLAUDE.md's layering), and building the entity list by hand here instead
/// would just restate the registry rather than checking it against the real model. Guards (c) and
/// (d), which only need reflection over the registry itself, live in
/// <c>FlightAssistant.Application.Tests.Administration.BrowsableTablesTests</c>.
///
/// Uses <c>UseInMemoryDatabase</c> purely to force the model to build (see
/// <c>FlightStatusConfigurationTests</c> for the same pattern) — nothing here queries data, so the
/// provider choice does not matter for these assertions.
/// </summary>
public class BrowsableTablesModelCompletenessTests
{
    private static FlightAssistantDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ── (a) Entity completeness ─────────────────────────────────────────────────
    // Every entity type in the EF model is either browsable or explicitly excluded with a reason.
    // This is the guard that must fail the moment someone adds a DbSet and stops there — the
    // central requirement of the #315 Task 1 brief. Built from ctx.Model.GetEntityTypes() rather
    // than a hand-typed list of the 19 types, so it actually checks the registry against the model
    // instead of restating it.

    [Fact]
    public void EveryEntityTypeInTheModel_IsBrowsableOrExplicitlyExcluded()
    {
        using var ctx = CreateContext();
        var modelTypes = ctx.Model.GetEntityTypes().Select(e => e.ClrType).ToList();

        Assert.NotEmpty(modelTypes); // sanity: the model actually loaded something

        var browsableTypes = BrowsableTables.All.Select(e => e.EntityType).ToHashSet();
        var notBrowsableTypes = BrowsableTables.NotBrowsable.Keys.ToHashSet();

        var unclassified = modelTypes
            .Where(t => !browsableTypes.Contains(t) && !notBrowsableTypes.Contains(t))
            .ToList();

        Assert.True(
            unclassified.Count == 0,
            "Entity type(s) in the EF model neither browsable nor excluded with a reason: " +
            string.Join(", ", unclassified.Select(t => t.Name)));
    }

    [Fact]
    public void EveryBrowsableEntry_NamesARealEntityTypeInTheModel()
    {
        using var ctx = CreateContext();
        var modelTypes = ctx.Model.GetEntityTypes().Select(e => e.ClrType).ToHashSet();

        foreach (var table in BrowsableTables.All)
        {
            Assert.True(
                modelTypes.Contains(table.EntityType),
                $"{table.Slug}: {table.EntityType.Name} is not a mapped entity type in the EF model.");
        }
    }

    [Fact]
    public void EveryNotBrowsableEntry_NamesARealEntityTypeInTheModel()
    {
        using var ctx = CreateContext();
        var modelTypes = ctx.Model.GetEntityTypes().Select(e => e.ClrType).ToHashSet();

        foreach (var (type, reason) in BrowsableTables.NotBrowsable)
        {
            Assert.True(modelTypes.Contains(type), $"{type.Name} (excluded: \"{reason}\") is not a mapped entity type in the EF model.");
        }
    }

    // ── (b) Column completeness ─────────────────────────────────────────────────
    // For every browsable entity, every mapped scalar property is either in BrowsableTables'
    // exposed list or in this test's own excluded-with-reason map. This is what stops a new
    // property being silently invisible in the browser — or, worse, a new *sensitive* property
    // being silently exposed because nobody re-ran the classification.
    //
    // This map records a mapped property that genuinely should not be browsable, with a reason,
    // instead of just making this test fail with no explanation — mirroring
    // BrowsableTables.NotBrowsable's shape at entity granularity, but kept in the test rather than the
    // production registry because BrowsableTables' own spec (Task 1 brief, section 1) only describes
    // an entity-level opt-out.
    private static readonly IReadOnlyDictionary<(Type EntityType, string Property), string> ExcludedColumns =
        new Dictionary<(Type, string), string>
        {
            // A shadow property mapped to PostgreSQL's xmin system column (#378): the verification job's
            // concurrency token. It has no CLR property for the browser's projection to read, and an
            // xmin value means nothing to an admin.
            [(typeof(VerificationRequest), VerificationRequestConfiguration.RowVersionPropertyName)] =
                "Row version (PostgreSQL xmin): a shadow concurrency token with no CLR property and no meaning to an admin",
        };

    [Fact]
    public void EveryMappedScalarProperty_IsExposedOrExplicitlyExcluded()
    {
        using var ctx = CreateContext();

        foreach (var table in BrowsableTables.All)
        {
            var entityType = ctx.Model.FindEntityType(table.EntityType);
            Assert.NotNull(entityType);

            var mappedScalarProperties = entityType!.GetProperties().Select(p => p.Name).ToList();

            var unaccountedFor = mappedScalarProperties
                .Where(p => !table.Columns.Contains(p, StringComparer.Ordinal)
                            && !ExcludedColumns.ContainsKey((table.EntityType, p)))
                .ToList();

            Assert.True(
                unaccountedFor.Count == 0,
                $"{table.Slug} ({table.EntityType.Name}): mapped property(ies) neither exposed nor excluded with a reason: " +
                string.Join(", ", unaccountedFor));
        }
    }

    /// <summary>
    /// The complement of the guard above, and of guard (c) in <c>BrowsableTablesTests</c>: (c)
    /// checks every exposed column resolves via plain CLR reflection, which a navigation property
    /// (e.g. <c>Flight.Airline</c>) would also pass — reflection cannot tell a scalar column from a
    /// reference navigation. This checks the model instead, so an exposed "column" that is actually
    /// a navigation or a <c>[NotMapped]</c> member is caught here.
    /// </summary>
    [Fact]
    public void EveryExposedColumn_IsAMappedScalarPropertyInTheModel()
    {
        using var ctx = CreateContext();

        foreach (var table in BrowsableTables.All)
        {
            var entityType = ctx.Model.FindEntityType(table.EntityType);
            Assert.NotNull(entityType);

            var mappedScalarProperties = entityType!.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var column in table.Columns)
            {
                Assert.True(
                    mappedScalarProperties.Contains(column),
                    $"{table.Slug}: '{column}' is listed as exposed but is not a mapped scalar property on the EF model " +
                    "(it may be a navigation, a shadow property under a different name, or [NotMapped]).");
            }
        }
    }

    [Fact]
    public void EveryExcludedColumnEntry_HasANonEmptyReason()
    {
        Assert.All(ExcludedColumns, kvp => Assert.False(string.IsNullOrWhiteSpace(kvp.Value)));
    }

    /// <summary>
    /// An exclusion is a decision not to expose the column, so a column cannot be both. Without this,
    /// listing an excluded column (such as <c>verification_requests</c>' xmin row version) as exposed
    /// passes both completeness guards above: it is a mapped property, and it is accounted for.
    /// </summary>
    [Fact]
    public void NoColumn_IsBothExposedAndExcluded()
    {
        var both = BrowsableTables.All
            .SelectMany(table => table.Columns
                .Where(column => ExcludedColumns.ContainsKey((table.EntityType, column)))
                .Select(column => $"{table.Slug}.{column}"))
            .ToList();

        Assert.True(both.Count == 0, "Column(s) both exposed and excluded: " + string.Join(", ", both));
    }

    /// <summary>
    /// The browser projects rows by CLR reflection, so a shadow property (one with no CLR member, such as
    /// the xmin row version) can never be read, and exposing one would make every read of that table a 500.
    /// </summary>
    [Fact]
    public void NoExposedColumn_IsAShadowProperty()
    {
        using var ctx = CreateContext();

        var shadow = BrowsableTables.All
            .SelectMany(table => table.Columns
                .Where(column => ctx.Model.FindEntityType(table.EntityType)?.FindProperty(column)?.IsShadowProperty() == true)
                .Select(column => $"{table.Slug}.{column}"))
            .ToList();

        Assert.True(shadow.Count == 0, "Exposed column(s) that are shadow properties: " + string.Join(", ", shadow));
    }
}
