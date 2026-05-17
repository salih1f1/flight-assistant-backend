using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportAirlinesCommandHandlerTests
{
    private readonly IAirlineRepository _repo = Substitute.For<IAirlineRepository>();

    [Fact]
    public async Task Handle_NewAirline_InsertsAndReturnsInsertedCount()
    {
        _repo.FindByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _repo.FindByIcaoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new ImportAirlinesCommandHandler(_repo);
        var dto = new AirlineImportDto("BA", "BAW", "British Airways", "GB", "OW");

        var result = await handler.Handle(new ImportAirlinesCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddAsync(Arg.Any<Airline>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingAirlineByIata_UpdatesAndReturnsUpdatedCount()
    {
        var existing = Airline.Create("BA", null, "Old Name", "GB", null);
        _repo.FindByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new ImportAirlinesCommandHandler(_repo);
        var dto = new AirlineImportDto("BA", "BAW", "British Airways", "GB", "OW");

        var result = await handler.Handle(new ImportAirlinesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddAsync(Arg.Any<Airline>(), Arg.Any<CancellationToken>());
        Assert.Equal("British Airways", existing.Name);
    }

    [Fact]
    public async Task Handle_IataNotFound_FallsBackToIcaoLookup()
    {
        var existing = Airline.Create(null, "BAW", "British Airways", "GB", null);
        _repo.FindByIataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        _repo.FindByIcaoAsync("BAW", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new ImportAirlinesCommandHandler(_repo);
        var dto = new AirlineImportDto(null, "BAW", "British Airways Updated", "GB", null);

        var result = await handler.Handle(new ImportAirlinesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Equal("British Airways Updated", existing.Name);
    }

    [Fact]
    public async Task Handle_ImportedAirline_IsNotVerified()
    {
        _repo.FindByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _repo.FindByIcaoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        Airline? added = null;
        await _repo.AddAsync(Arg.Do<Airline>(a => added = a), Arg.Any<CancellationToken>());
        var handler = new ImportAirlinesCommandHandler(_repo);
        var dto = new AirlineImportDto("BA", null, "British Airways", "GB", null);

        await handler.Handle(new ImportAirlinesCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.False(added!.IsVerified);
    }
}
