using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Export;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Export;

public class ExportLoungesQueryHandlerTests
{
    private readonly ILoungeRepository _loungeRepo = Substitute.For<ILoungeRepository>();
    private readonly IRefDataRepository _refDataRepo = Substitute.For<IRefDataRepository>();

    [Fact]
    public async Task Handle_ReturnsVerifiedLoungesWithAirportIataAndProviders()
    {
        var lounge = Lounge.Create(1, "Galleries Club", "T3", null, "06:00-22:00", true, false, false, "WiFi");
        var link = LoungeProviderLink.Create(0, 5, LoungeProviderRole.Operator, "Members only", "GOLD");
        lounge.ReplaceAllProviders(new[] { link });
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        lounge.Verify("admin@example.com", "manual", verifiedAt);

        _loungeRepo.GetAllVerifiedWithLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Lounge> { lounge });
        _refDataRepo.GetAirportIataMapAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(1)),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { { 1, "LHR" } });
        _refDataRepo.GetLoungeProviderCodeMapAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(5)),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { { 5, "PP" } });

        var handler = new ExportLoungesQueryHandler(_loungeRepo, _refDataRepo);
        var result = await handler.Handle(new ExportLoungesQuery(), CancellationToken.None);

        Assert.Single(result);
        var dto = result[0];
        Assert.Equal("Galleries Club", dto.Name);
        Assert.Equal("LHR", dto.AirportIata);
        Assert.Equal("T3", dto.Terminal);
        Assert.Equal("06:00-22:00", dto.Hours);
        Assert.True(dto.IsAirside);
        Assert.Single(dto.Providers);
        Assert.Equal("PP", dto.Providers[0].ProviderCode);
        Assert.Equal("Operator", dto.Providers[0].Role);
        Assert.Equal("Members only", dto.Providers[0].Notes);
        Assert.Equal("GOLD", dto.Providers[0].MinAllianceTierCode);
        Assert.Equal(verifiedAt, dto.VerifiedAt);
        Assert.Equal("admin@example.com", dto.VerifiedBy);
        Assert.Equal("manual", dto.VerificationSource);
    }

    [Fact]
    public async Task Handle_NoVerifiedLounges_DoesNotCallRefData()
    {
        _loungeRepo.GetAllVerifiedWithLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Lounge>());

        var handler = new ExportLoungesQueryHandler(_loungeRepo, _refDataRepo);
        var result = await handler.Handle(new ExportLoungesQuery(), CancellationToken.None);

        Assert.Empty(result);
        await _refDataRepo.DidNotReceive()
            .GetAirportIataMapAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }
}
