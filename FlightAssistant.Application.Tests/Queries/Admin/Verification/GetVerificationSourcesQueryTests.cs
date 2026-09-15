using FlightAssistant.Application.Queries.Admin.Verification;
using FlightAssistant.Application.Tests.Verification;
using FlightAssistant.Domain.Verification.Enums;
using static FlightAssistant.Application.Tests.Commands.Admin.Verification.TestSources;

namespace FlightAssistant.Application.Tests.Queries.Admin.Verification;

/// <summary><see cref="GetVerificationSourcesQuery"/> (#378): which sources an admin can pick for an entity type.</summary>
public class GetVerificationSourcesQueryTests
{
    private static readonly FakeVerificationSourceRegistry Registry = new(
        Source("ourairports", 0m, VerificationEntityType.Airport),
        Source("mledoze", 0m, VerificationEntityType.Country),
        Source("aeroapi", 0.015m, VerificationEntityType.Airport, VerificationEntityType.Airline),
        Source("both", 0.002m, VerificationEntityType.Airport, VerificationEntityType.Country));

    private static Task<IReadOnlyList<AdminVerificationSourceResult>> Handle(string entityType) =>
        new GetVerificationSourcesQueryHandler(Registry).Handle(new GetVerificationSourcesQuery(entityType), CancellationToken.None);

    [Fact]
    public async Task Handle_ReturnsOnlyTheSourcesSupportingTheType_OrderedByName_WithTheirCost()
    {
        var sources = await Handle("Airport");

        Assert.Equal(
            new[]
            {
                new AdminVerificationSourceResult("aeroapi", 0.015m),
                new AdminVerificationSourceResult("both", 0.002m),
                new AdminVerificationSourceResult("ourairports", 0m)
            },
            sources);
    }

    [Fact]
    public async Task Handle_Country_ReturnsTheCountrySources()
    {
        var sources = await Handle("Country");

        Assert.Equal(new[] { "both", "mledoze" }, sources.Select(s => s.Name));
    }

    [Fact]
    public async Task Handle_ATypeNoSourceSupports_ReturnsEmpty()
    {
        var registry = new FakeVerificationSourceRegistry(Source("ourairports", 0m, VerificationEntityType.Airport));

        var sources = await new GetVerificationSourcesQueryHandler(registry)
            .Handle(new GetVerificationSourcesQuery("Airline"), CancellationToken.None);

        Assert.Empty(sources);
    }

    [Theory]
    [InlineData("Airport")]
    [InlineData("Airline")]
    [InlineData("Country")]
    public void Validator_AcceptsEachExactName(string entityType)
    {
        Assert.True(new GetVerificationSourcesQueryValidator().Validate(new GetVerificationSourcesQuery(entityType)).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("airport")] // exact names only
    [InlineData("AIRPORT")]
    [InlineData(" Airport")]
    [InlineData("0")] // Enum.TryParse would accept a number
    [InlineData("Lounge")]
    public void Validator_RejectsAnythingButAnExactName(string? entityType)
    {
        var result = new GetVerificationSourcesQueryValidator().Validate(new GetVerificationSourcesQuery(entityType));

        var failure = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetVerificationSourcesQuery.EntityType), failure.PropertyName);
    }
}
