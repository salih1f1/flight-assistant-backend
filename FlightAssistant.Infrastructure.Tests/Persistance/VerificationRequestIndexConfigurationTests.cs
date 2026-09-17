using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Configurations.Verification;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// The <c>verification_requests</c> partial indexes, checked against EF model metadata like
/// <c>FlightIndexConfigurationTests</c> checks <see cref="Domain.Flights.Entities.Flight"/>'s: the
/// InMemory provider never issues real SQL and so can't prove a partial filter is enforced at the
/// database, but it does prove the model carries the columns, order and filter this configuration
/// intends -- catching a filter or column dropped by accident before a real-SQL test (#418's Task 2,
/// Infrastructure's Testcontainers-backed verification suite) would have to.
/// </summary>
public class VerificationRequestIndexConfigurationTests
{
    private static FlightAssistantDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// The error-cooldown lookup's supporting index (#418): (EntityType, EntityKey, CompletedUtc),
    /// partial on <c>completed_utc IS NOT NULL</c> -- not <c>status = 'Error'</c> (fix round 1):
    /// EXPLAIN against 248k seeded rows proved a status='Error' partial index cannot serve the
    /// cooldown query's anti-join half ("is there a newer completion of any status"), forcing a full
    /// sequential scan every time a batch contained a cooling key. Filtering on <c>completed_utc IS
    /// NOT NULL</c> instead covers every completed row, so it serves both halves of the query from
    /// the one index. Column order mirrors how Task 2's query uses it: EntityType is a single
    /// equality per call, EntityKey is matched with an IN over a batch of keys (one equality per
    /// key), and CompletedUtc trails so Postgres can apply the cutoff as an index condition within
    /// each per-key scan rather than filtering after the fact. Not unique: the same entity completes
    /// more than once over time, and the table is never purged.
    /// </summary>
    [Fact]
    public void ErrorCooldownIndex_IsPartialOnCompletedRows_WithTheExpectedColumnsAndOrder()
    {
        using var ctx = CreateContext();

        var entity = ctx.Model.FindEntityType(typeof(VerificationRequest))!;

        var index = entity.GetIndexes().Single(ix =>
            ix.Properties.Select(p => p.Name).SequenceEqual(
                new[] { nameof(VerificationRequest.EntityType), nameof(VerificationRequest.EntityKey), nameof(VerificationRequest.CompletedUtc) }));

        Assert.Equal(VerificationRequestConfiguration.ErrorCooldownIndexName, index.GetDatabaseName());
        Assert.False(index.IsUnique);
        Assert.Equal("\"completed_utc\" IS NOT NULL", index.GetFilter());
    }

    /// <summary>
    /// The 24-hour Error-attribution report's supporting index (#424): leads on CompletedUtc (the
    /// query's only range predicate: "WHERE status = 'Error' AND completed_utc >= @windowStart GROUP
    /// BY failed_source, entity_type"), partial on <c>status = 'Error'</c> -- correct here, unlike
    /// <see cref="VerificationRequestConfiguration.ErrorCooldownIndexName"/>, because this query only
    /// ever wants Error rows. FailedSource and EntityType ride as INCLUDE columns so the GROUP BY can
    /// be answered from the index alone. Break-proof: dropping the status filter from the
    /// configuration fails this test's filter assertion.
    /// </summary>
    [Fact]
    public void ErrorAttributionIndex_IsPartialOnErrorRows_LeadingOnCompletedUtc_WithFailedSourceAndEntityTypeIncluded()
    {
        using var ctx = CreateContext();

        var entity = ctx.Model.FindEntityType(typeof(VerificationRequest))!;

        var index = entity.GetIndexes().Single(ix =>
            ix.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(VerificationRequest.CompletedUtc) }));

        Assert.Equal(VerificationRequestConfiguration.ErrorAttributionIndexName, index.GetDatabaseName());
        Assert.False(index.IsUnique);
        Assert.Equal("\"status\" = 'Error'", index.GetFilter());
        Assert.Equal(
            new[] { nameof(VerificationRequest.FailedSource), nameof(VerificationRequest.EntityType) },
            index.GetIncludeProperties());
    }
}
