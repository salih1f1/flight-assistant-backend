using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportLoyaltyProgramKindsCommandHandlerTests
{
    private readonly ILoyaltyRepository _repo = Substitute.For<ILoyaltyRepository>();

    [Fact]
    public async Task Handle_NewKind_InsertsAndReturnsInsertedCount()
    {
        _repo.FindKindByCodeAsync("AIRLINE", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new ImportLoyaltyProgramKindsCommandHandler(_repo);
        var dto = new LoyaltyProgramKindImportDto("AIRLINE", "Airline Miles", "Earn miles on flights");

        var result = await handler.Handle(new ImportLoyaltyProgramKindsCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddKindAsync(Arg.Any<LoyaltyProgramKind>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingKind_UpdatesAndReturnsUpdatedCount()
    {
        var existing = LoyaltyProgramKind.Create("AIRLINE", "Old Name", null);
        _repo.FindKindByCodeAsync("AIRLINE", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new ImportLoyaltyProgramKindsCommandHandler(_repo);
        var dto = new LoyaltyProgramKindImportDto("AIRLINE", "Airline Miles", "Updated description");

        var result = await handler.Handle(new ImportLoyaltyProgramKindsCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddKindAsync(Arg.Any<LoyaltyProgramKind>(), Arg.Any<CancellationToken>());
        Assert.Equal("Airline Miles", existing.Name);
    }
}
