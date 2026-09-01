using System.Reflection;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// Coverage test: every status code <see cref="AeroApiFlightStatusProvider"/> can actually emit
/// must exist in the <c>flight_statuses</c> seed (#281), so that adding a new provider status code
/// without seeding it fails a test rather than surfacing in production as a sync that has already
/// spent a billable AeroAPI call and then falls back to "UNK" (or, pre-#281, threw outright).
///
/// <para>
/// The provider's status vocabulary is read straight from its source — the private
/// <c>Status*</c> const string fields backing <c>MapStatusCode</c> (e.g. <c>StatusScheduled =
/// "SCH"</c>) — via reflection, rather than duplicating a second, hand-copied literal list here
/// that could silently drift from the real one.
/// </para>
/// </summary>
public class FlightStatusCodeCoverageTests
{
    private static IReadOnlyList<string> ProviderEmittedCodes() =>
        typeof(AeroApiFlightStatusProvider)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral
                        && f.FieldType == typeof(string)
                        && f.Name.StartsWith("Status", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    [Fact]
    public void ProviderEmittedCodes_AreNotEmpty()
    {
        // Guards the reflection itself: if the "Status*" naming convention it relies on ever
        // changes and the query silently starts matching nothing, every other assertion below
        // would pass vacuously and this test would stop doing its job without failing.
        Assert.NotEmpty(ProviderEmittedCodes());
    }

    [Fact]
    public void EveryProviderEmittedCode_ExistsInTheSeededFlightStatuses()
    {
        using var ctx = new FlightAssistantDbContext(
            new DbContextOptionsBuilder<FlightAssistantDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        // HasData seed rows live only in the design-time model — see FlightStatusConfigurationTests
        // for why ctx.Model (the runtime model) can't be used here.
        var seededCodes = ctx.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(FlightStatus))!
            .GetSeedData()
            .Select(row => (string)row["Code"]!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var code in ProviderEmittedCodes())
            Assert.Contains(code, seededCodes);
    }
}
