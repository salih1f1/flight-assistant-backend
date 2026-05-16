using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportLoyaltyProgramsCommandHandlerTests
{
    private readonly ILoyaltyRepository _loyaltyRepo = Substitute.For<ILoyaltyRepository>();
    private readonly IRefDataRepository _refRepo = Substitute.For<IRefDataRepository>();

    [Fact]
    public async Task Handle_NewProgram_ResolvesKindAndAirlineThenInserts()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        _refRepo.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(5);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync("BAEC", Arg.Any<CancellationToken>()).ReturnsNull();

        var handler = new ImportLoyaltyProgramsCommandHandler(_loyaltyRepo, _refRepo);
        var dto = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", "BA", null, []);

        var result = await handler.Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _loyaltyRepo.Received(1).AddProgramAsync(Arg.Any<LoyaltyProgram>(), Arg.Any<CancellationToken>());
        await _loyaltyRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnknownKindCode_AddsErrorAndSkipsRecord()
    {
        _loyaltyRepo.FindKindByCodeAsync("unknown", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new ImportLoyaltyProgramsCommandHandler(_loyaltyRepo, _refRepo);
        var dto = new LoyaltyProgramImportDto("BAEC", "BA Executive Club", "unknown", null, null, []);

        var result = await handler.Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Single(result.Errors);
        Assert.Contains("unknown", result.Errors[0]);
    }

    [Fact]
    public async Task Handle_ExistingProgram_ReplacesTiersAndUpdates()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        _refRepo.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(5);
        var existing = LoyaltyProgram.Create("BAEC", "Old Name", kind.Id);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync("BAEC", Arg.Any<CancellationToken>()).Returns(existing);

        var handler = new ImportLoyaltyProgramsCommandHandler(_loyaltyRepo, _refRepo);
        var dto = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", "BA", null,
            [new LoyaltyTierImportDto("BLUE", "Blue", 1, null, null)]);

        var result = await handler.Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        await _loyaltyRepo.DidNotReceive().AddProgramAsync(Arg.Any<LoyaltyProgram>(), Arg.Any<CancellationToken>());
        await _loyaltyRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
