using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// Verifies that the (AirlineId, FlightNumber, ServiceDateUtc) lookup index on <see cref="Flight"/>
/// is configured as UNIQUE so the database prevents duplicate flight rows for the same
/// airline/number/service-date (issue #240). The in-memory provider does not enforce unique
/// indexes at runtime, so this asserts against the EF model metadata instead.
/// </summary>
public class FlightIndexConfigurationTests
{
    private static FlightAssistantDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public void FlightLookupIndex_IsUnique()
    {
        using var ctx = CreateContext();

        var entity = ctx.Model.FindEntityType(typeof(Flight))!;

        var lookupIndex = entity.GetIndexes().Single(ix =>
            ix.Properties.Select(p => p.Name).SequenceEqual(
                new[] { nameof(Flight.AirlineId), nameof(Flight.FlightNumber), nameof(Flight.ServiceDateUtc) }));

        Assert.True(lookupIndex.IsUnique);
        Assert.Equal("ix_flight_lookup", lookupIndex.GetDatabaseName());
    }
}
