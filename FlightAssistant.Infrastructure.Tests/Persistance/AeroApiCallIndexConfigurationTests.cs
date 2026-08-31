using FlightAssistant.Domain.Configuration.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// <c>aeroapi_calls</c> takes one INSERT per outbound AeroAPI call and a bulk DELETE every day from
/// the retention prune, so every index on it is paid for on the write path. It needs exactly one:
/// (CalledUtc, EndpointTemplate). A second index on CalledUtc alone would be a strict prefix of
/// that composite — Postgres serves a leading-column range scan straight from the composite — so it
/// would buy nothing and cost every insert. Asserted against the EF model rather than a database,
/// the same way <see cref="FlightIndexConfigurationTests"/> does.
/// </summary>
public class AeroApiCallIndexConfigurationTests
{
    private static FlightAssistantDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public void AeroApiCall_HasExactlyOneIndex_TheCalledUtcEndpointTemplateComposite()
    {
        using var ctx = CreateContext();

        var entity = ctx.Model.FindEntityType(typeof(AeroApiCall))!;

        var index = Assert.Single(entity.GetIndexes());
        Assert.Equal(
            new[] { nameof(AeroApiCall.CalledUtc), nameof(AeroApiCall.EndpointTemplate) },
            index.Properties.Select(p => p.Name));
        Assert.False(index.IsUnique);
    }
}
