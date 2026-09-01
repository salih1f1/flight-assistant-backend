using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// Asserts the <c>flight_statuses</c> seed declared via <c>FlightStatusConfiguration.HasData</c>
/// (#281) directly against the EF model rather than by querying a test <see cref="FlightAssistantDbContext"/>:
/// this solution's tests run on <c>UseInMemoryDatabase</c>, which does not apply <c>HasData</c>
/// seeding the way a relational provider does, so querying a test context for these rows would
/// silently pass against an empty table. Reading <c>Model.FindEntityType(...).GetSeedData()</c> is
/// provider-independent and asserts exactly what <c>HasData</c> declares — the same source EF turns
/// into the migration's <c>InsertData</c>.
/// </summary>
public class FlightStatusConfigurationTests
{
    private static readonly string[] ExpectedCodes = ["SCH", "DEP", "ARR", "DLY", "CAN", "DIV", "UNK"];

    private static FlightAssistantDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// <c>HasData</c> seed rows live only in the design-time model — <c>ctx.Model</c> exposes the
    /// runtime "read-optimized" model, which strips them and throws
    /// <c>InvalidOperationException</c> from <c>GetSeedData()</c> if asked. This is the
    /// documented way to reach the design-time model from a live context.
    /// </summary>
    private static IEntityType FlightStatusEntityType(FlightAssistantDbContext ctx) =>
        ctx.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(FlightStatus))!;

    private static List<IDictionary<string, object?>> SeedRows()
    {
        using var ctx = CreateContext();
        return FlightStatusEntityType(ctx).GetSeedData().ToList();
    }

    [Fact]
    public void SeedData_ContainsExactlyTheSevenDocumentedCodes()
    {
        var codes = SeedRows().Select(r => (string)r["Code"]!).ToList();

        Assert.Equal(ExpectedCodes.Length, codes.Count);
        foreach (var expected in ExpectedCodes)
            Assert.Contains(expected, codes);
    }

    [Fact]
    public void SeedData_IdsAreUnique()
    {
        var ids = SeedRows().Select(r => (int)r["Id"]!).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void SeedData_CodesAreUnique()
    {
        var codes = SeedRows().Select(r => (string)r["Code"]!).ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Guards every string column against its own configured max length — not just <c>Code</c>
    /// (32) but <c>Name</c> (64) and <c>Description</c> (4000) too, read from the model's own
    /// metadata rather than hand-copied numbers, so this stays correct if a column's length is
    /// ever changed.
    /// </summary>
    [Fact]
    public void SeedData_NoFieldExceedsItsConfiguredColumnLength()
    {
        using var ctx = CreateContext();
        var entity = FlightStatusEntityType(ctx);
        var maxLengths = entity.GetProperties()
            .Where(p => p.GetMaxLength() is not null)
            .ToDictionary(p => p.Name, p => p.GetMaxLength()!.Value);

        Assert.NotEmpty(maxLengths); // sanity check the metadata read actually found columns

        foreach (var row in entity.GetSeedData())
        {
            foreach (var (property, maxLength) in maxLengths)
            {
                if (row.TryGetValue(property, out var value) && value is string s)
                {
                    Assert.True(
                        s.Length <= maxLength,
                        $"{property} value '{s}' ({s.Length} chars) exceeds the configured max length of {maxLength}.");
                }
            }
        }
    }
}
