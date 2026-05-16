using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportAirportsCommandHandlerTests
{
    private readonly IAirportRepository _repo = Substitute.For<IAirportRepository>();

    [Fact]
    public async Task Handle_NewAirport_InsertsAndReturnsInsertedCount()
    {
        _repo.FindByIataAsync("LHR", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new ImportAirportsCommandHandler(_repo);
        var dto = new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", 51.477, -0.461, 83, "Europe/London");

        var result = await handler.Handle(new ImportAirportsCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddAsync(Arg.Any<Airport>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingAirport_UpdatesAndReturnsUpdatedCount()
    {
        var existing = Airport.Create("LHR", null, "Old Name", "London", "GB");
        _repo.FindByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = new ImportAirportsCommandHandler(_repo);
        var dto = new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", 51.477, -0.461, 83, "Europe/London");

        var result = await handler.Handle(new ImportAirportsCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddAsync(Arg.Any<Airport>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ImportedAirport_IsNotVerified()
    {
        _repo.FindByIataAsync("LHR", Arg.Any<CancellationToken>()).ReturnsNull();
        Airport? added = null;
        await _repo.AddAsync(Arg.Do<Airport>(a => added = a), Arg.Any<CancellationToken>());
        var handler = new ImportAirportsCommandHandler(_repo);
        var dto = new AirportImportDto("LHR", null, "Heathrow", "London", "GB", null, null, null, null);

        await handler.Handle(new ImportAirportsCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.False(added!.IsVerified);
    }
}
