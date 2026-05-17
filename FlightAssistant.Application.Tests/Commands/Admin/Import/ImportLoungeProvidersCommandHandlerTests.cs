using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportLoungeProvidersCommandHandlerTests
{
    private readonly ILoungeRepository _repo = Substitute.For<ILoungeRepository>();

    [Fact]
    public async Task Handle_NewProvider_InsertsAndReturnsInsertedCount()
    {
        _repo.FindProviderByCodeAsync("AMEX", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new ImportLoungeProvidersCommandHandler(_repo);
        var dto = new LoungeProviderImportDto("AMEX", "American Express", "Card", null);

        var result = await handler.Handle(new ImportLoungeProvidersCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddProviderAsync(Arg.Any<LoungeProvider>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingProvider_UpdatesAndReturnsUpdatedCount()
    {
        var existing = LoungeProvider.Create("AMEX", "Old Name", LoungeProviderKind.Card, null);
        _repo.FindProviderByCodeAsync("AMEX", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new ImportLoungeProvidersCommandHandler(_repo);
        var dto = new LoungeProviderImportDto("AMEX", "American Express", "Card", "Premium card lounge access");

        var result = await handler.Handle(new ImportLoungeProvidersCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddProviderAsync(Arg.Any<LoungeProvider>(), Arg.Any<CancellationToken>());
        Assert.Equal("American Express", existing.Name);
    }

    [Fact]
    public async Task Handle_InvalidKind_AddsErrorAndSkips()
    {
        var handler = new ImportLoungeProvidersCommandHandler(_repo);
        var dto = new LoungeProviderImportDto("XYZ", "Unknown", "NotAValidKind", null);

        var result = await handler.Handle(new ImportLoungeProvidersCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Single(result.Errors);
        Assert.Contains("NotAValidKind", result.Errors[0]);
        await _repo.DidNotReceive().AddProviderAsync(Arg.Any<LoungeProvider>(), Arg.Any<CancellationToken>());
    }
}
