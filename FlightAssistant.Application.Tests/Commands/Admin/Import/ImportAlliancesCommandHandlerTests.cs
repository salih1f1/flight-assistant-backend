using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportAlliancesCommandHandlerTests
{
    private readonly IAllianceRepository _repo = Substitute.For<IAllianceRepository>();

    [Fact]
    public async Task Handle_NewAlliance_InsertsAndReturnsInsertedCount()
    {
        _repo.FindByCodeAsync("SA", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new ImportAlliancesCommandHandler(_repo);
        var dto = new AllianceImportDto("SA", "Star Alliance");

        var result = await handler.Handle(new ImportAlliancesCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddAsync(Arg.Any<Alliance>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingAlliance_UpdatesAndReturnsUpdatedCount()
    {
        var existing = Alliance.Create("SA", "Old Name");
        _repo.FindByCodeAsync("SA", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new ImportAlliancesCommandHandler(_repo);
        var dto = new AllianceImportDto("SA", "Star Alliance");

        var result = await handler.Handle(new ImportAlliancesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddAsync(Arg.Any<Alliance>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal("Star Alliance", existing.Name);
    }
}
