using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Export;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportLoungesCommandHandlerTests
{
    private readonly ILoungeRepository _loungeRepo = Substitute.For<ILoungeRepository>();
    private readonly IRefDataRepository _refRepo = Substitute.For<IRefDataRepository>();

    [Fact]
    public async Task Handle_NewLounge_ResolvesAirportAndProviderThenInserts()
    {
        _refRepo.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(10);
        _loungeRepo.FindProviderByCodeAsync("PLAZA", Arg.Any<CancellationToken>()).Returns(LoungeProvider.Create("PLAZA", "Plaza Premium", LoungeProviderKind.Contract));
        _loungeRepo.FindByNaturalKeyWithProvidersAsync(10, "Plaza Premium Lounge", null, Arg.Any<CancellationToken>()).ReturnsNull();
        Lounge? added = null;
        await _loungeRepo.AddAsync(Arg.Do<Lounge>(l => added = l), Arg.Any<CancellationToken>());

        var handler = new ImportLoungesCommandHandler(_loungeRepo, _refRepo);
        var dto = new LoungeImportDto("Plaza Premium Lounge", "LHR", null, null, null, true, false, false, null,
            [new LoungeProviderLinkImportDto("PLAZA", "Operator", null, null)]);

        var result = await handler.Handle(new ImportLoungesCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        Assert.NotNull(added);
        Assert.False(added!.IsVerified);
    }

    [Fact]
    public async Task Handle_UnknownAirportIata_AddsErrorAndSkipsRecord()
    {
        _refRepo.GetAirportIdByIataAsync("ZZZ", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new ImportLoungesCommandHandler(_loungeRepo, _refRepo);
        var dto = new LoungeImportDto("Some Lounge", "ZZZ", null, null, null, true, false, false, null, []);

        var result = await handler.Handle(new ImportLoungesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Single(result.Errors);
        Assert.Contains("ZZZ", result.Errors[0]);
    }

    [Fact]
    public async Task Handle_ExistingLounge_ReplacesProvidersAndUpdates()
    {
        var existingLounge = Lounge.Create(10, "Plaza Premium Lounge");
        _refRepo.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(10);
        _loungeRepo.FindByNaturalKeyWithProvidersAsync(10, "Plaza Premium Lounge", "T1", Arg.Any<CancellationToken>()).Returns(existingLounge);
        _loungeRepo.FindProviderByCodeAsync("PLAZA", Arg.Any<CancellationToken>()).Returns(LoungeProvider.Create("PLAZA", "Plaza Premium", LoungeProviderKind.Contract));

        var handler = new ImportLoungesCommandHandler(_loungeRepo, _refRepo);
        var dto = new LoungeImportDto("Plaza Premium Lounge", "LHR", "T1", null, null, true, false, false, null,
            [new LoungeProviderLinkImportDto("PLAZA", "Operator", null, null)]);

        var result = await handler.Handle(new ImportLoungesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        await _loungeRepo.DidNotReceive().AddAsync(Arg.Any<Lounge>(), Arg.Any<CancellationToken>());
        await _loungeRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
