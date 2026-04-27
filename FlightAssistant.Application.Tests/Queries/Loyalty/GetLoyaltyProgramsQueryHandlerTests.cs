using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Loyalty;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Loyalty;

public class GetLoyaltyProgramsQueryHandlerTests
{
    private readonly ILoyaltyRepository _loyalty = Substitute.For<ILoyaltyRepository>();
    private readonly GetLoyaltyProgramsQueryHandler _handler;

    public GetLoyaltyProgramsQueryHandlerTests()
    {
        _handler = new GetLoyaltyProgramsQueryHandler(_loyalty);
    }

    [Fact]
    public async Task ReturnsPaginatedPrograms_WithKindAndTiers()
    {
        var kind = LoyaltyProgramKind.Create("AIRLINE", "Airline Loyalty");
        var program = LoyaltyProgram.Create("BA", "British Airways Executive Club", kindId: 1, airlineId: 5);
        // Attach Kind via reflection because it's set by EF in production; use the Create method for tests
        var tier = LoyaltyTier.Create(programId: 1, "BRONZE", "Bronze", rank: 1);
        program.AddTier(tier);

        // We need to set the Kind navigation property — use a program with Kind hydrated
        var hydrated = CreateProgramWithKind("BA", "British Airways Executive Club", kind, airlineId: 5);
        hydrated.AddTier(tier);

        _loyalty.GetProgramsAsync(1, 50, Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<LoyaltyProgram>)[hydrated], 1));

        var query = new GetLoyaltyProgramsQuery(new PagedRequest(1, 50));
        var result = await _handler.Handle(query, CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("BA", item.Code);
        Assert.Equal("AIRLINE", item.Kind);
        Assert.Single(item.Tiers);
        Assert.Equal("BRONZE", item.Tiers[0].Code);
    }

    [Fact]
    public async Task EmptyList_ReturnsEmptyPage()
    {
        _loyalty.GetProgramsAsync(1, 50, Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<LoyaltyProgram>)[], 0));

        var result = await _handler.Handle(new GetLoyaltyProgramsQuery(new PagedRequest(1, 50)), CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }

    /// <summary>Helper to build a LoyaltyProgram with its Kind navigation property set.</summary>
    private static LoyaltyProgram CreateProgramWithKind(string code, string name, LoyaltyProgramKind kind, int? airlineId = null)
    {
        var p = LoyaltyProgram.Create(code, name, kindId: kind.Id, airlineId: airlineId);
        // Kind is a navigation property set by EF; wire it via reflection for unit tests
        typeof(LoyaltyProgram)
            .GetProperty(nameof(LoyaltyProgram.Kind))!
            .SetValue(p, kind);
        return p;
    }
}
