using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Export;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Export;

public class ExportLoyaltyProgramsQueryHandlerTests
{
    private readonly ILoyaltyRepository _loyaltyRepo = Substitute.For<ILoyaltyRepository>();
    private readonly IRefDataRepository _refDataRepo = Substitute.For<IRefDataRepository>();

    [Fact]
    public async Task Handle_MapsKindCodeAndAirlineIataAndTiers()
    {
        var program = LoyaltyProgram.Create("BAEC", "BA Executive Club", kindId: 1, airlineId: 2);
        program.AddTier(LoyaltyTier.Create(0, "Bronze", "Bronze", 1, "OW", "ONEWORLD_BRONZE"));
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        program.Verify("admin@example.com", "manual", verifiedAt);

        _loyaltyRepo.GetAllVerifiedWithTiersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LoyaltyProgram> { program });
        _loyaltyRepo.GetKindCodeMapAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { { 1, "Airline" } });
        _refDataRepo.GetAirlineIataMapAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(2)),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { { 2, "BA" } });

        var handler = new ExportLoyaltyProgramsQueryHandler(_loyaltyRepo, _refDataRepo);
        var result = await handler.Handle(new ExportLoyaltyProgramsQuery(), CancellationToken.None);

        Assert.Single(result);
        var dto = result[0];
        Assert.Equal("BAEC", dto.Code);
        Assert.Equal("BA Executive Club", dto.Name);
        Assert.Equal("Airline", dto.KindCode);
        Assert.Equal("BA", dto.AirlineIata);
        Assert.Single(dto.Tiers);
        Assert.Equal("Bronze", dto.Tiers[0].Code);
        Assert.Equal(1, dto.Tiers[0].Rank);
        Assert.Equal("OW", dto.Tiers[0].AllianceCode);
        Assert.Equal("ONEWORLD_BRONZE", dto.Tiers[0].AllianceTierCode);
        Assert.Equal(verifiedAt, dto.VerifiedAt);
        Assert.Equal("admin@example.com", dto.VerifiedBy);
        Assert.Equal("manual", dto.VerificationSource);
    }

    [Fact]
    public async Task Handle_ProgramWithoutAirlineId_MapsNullAirlineIata()
    {
        var program = LoyaltyProgram.Create("FFMM", "Miles & More", kindId: 1, airlineId: null);

        _loyaltyRepo.GetAllVerifiedWithTiersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<LoyaltyProgram> { program });
        _loyaltyRepo.GetKindCodeMapAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { { 1, "Airline" } });

        var handler = new ExportLoyaltyProgramsQueryHandler(_loyaltyRepo, _refDataRepo);
        var result = await handler.Handle(new ExportLoyaltyProgramsQuery(), CancellationToken.None);

        Assert.Single(result);
        Assert.Null(result[0].AirlineIata);
        await _refDataRepo.DidNotReceive()
            .GetAirlineIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }
}
