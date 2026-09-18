using FlightAssistant.Application.Commands.Admin.Airlines;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin;

public class AdminAirlineCommandHandlerTests
{
    private readonly IAirlineRepository _repo = Substitute.For<IAirlineRepository>();
    private readonly IAdminFlightRepository _flights = Substitute.For<IAdminFlightRepository>();
    private readonly IAllianceRepository _alliances = Substitute.For<IAllianceRepository>();

    /// <summary>Makes <paramref name="code"/> a real row, so the alliance guard lets it through.</summary>
    private void GivenAlliance(string code) =>
        _alliances.FindByCodeAsync(code, Arg.Any<CancellationToken>())
            .Returns(Alliance.Create(code, $"{code} Alliance"));

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_CallsAddAndSave_ReturnsId()
    {
        var handler = new CreateAirlineCommandHandler(_repo, _alliances);
        _repo.AddAsync(Arg.Any<Airline>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var id = await handler.Handle(
            new CreateAirlineCommand("BA", null, "British Airways", "GB", null),
            CancellationToken.None);

        await _repo.Received(1).AddAsync(Arg.Any<Airline>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ExistingAirline_CallsSave()
    {
        var handler = new UpdateAirlineCommandHandler(_repo, _alliances);
        var airline = Airline.Create("BA", null, "British Airways", "GB");
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(airline);

        await handler.Handle(
            new UpdateAirlineCommand(1, "BA", null, "British Airways Updated", "GB", null),
            CancellationToken.None);

        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_NotFound_Throws()
    {
        var handler = new UpdateAirlineCommandHandler(_repo, _alliances);
        _repo.GetByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(
                new UpdateAirlineCommand(99, "BA", null, "British Airways", "GB", null),
                CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);

        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Alliance code guard (#449) ────────────────────────────────────────────

    [Fact]
    public async Task Create_AllianceCodeThatIsNotAnAlliance_Throws409AndSavesNothing()
    {
        var handler = new CreateAirlineCommandHandler(_repo, _alliances);
        _alliances.FindByCodeAsync("STAR", Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(
                new CreateAirlineCommand("BA", null, "British Airways", "GB", "STAR"),
                CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("STAR", ex.Message);
        await _repo.DidNotReceive().AddAsync(Arg.Any<Airline>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_AllianceCodeThatExists_Saves()
    {
        var handler = new CreateAirlineCommandHandler(_repo, _alliances);
        GivenAlliance("OW");

        await handler.Handle(
            new CreateAirlineCommand("BA", null, "British Airways", "GB", "OW"),
            CancellationToken.None);

        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_NoAllianceCode_SavesWithoutLookingAnAllianceUp(string? allianceCode)
    {
        var handler = new CreateAirlineCommandHandler(_repo, _alliances);

        await handler.Handle(
            new CreateAirlineCommand("BA", null, "British Airways", "GB", allianceCode),
            CancellationToken.None);

        await _alliances.DidNotReceive().FindByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_AllianceCodeThatIsNotAnAlliance_Throws409AndSavesNothing()
    {
        var handler = new UpdateAirlineCommandHandler(_repo, _alliances);
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(Airline.Create("BA", null, "British Airways", "GB"));
        _alliances.FindByCodeAsync("STAR", Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(
                new UpdateAirlineCommand(1, "BA", null, "British Airways", "GB", "STAR"),
                CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("STAR", ex.Message);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_AllianceCodeThatExists_Saves()
    {
        var handler = new UpdateAirlineCommandHandler(_repo, _alliances);
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(Airline.Create("BA", null, "British Airways", "GB"));
        GivenAlliance("OW");

        await handler.Handle(
            new UpdateAirlineCommand(1, "BA", null, "British Airways", "GB", "OW"),
            CancellationToken.None);

        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_LowerCaseAllianceCode_IsAcceptedBecauseTheLookupCanonicalisesIt()
    {
        var handler = new UpdateAirlineCommandHandler(_repo, _alliances);
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(Airline.Create("BA", null, "British Airways", "GB"));
        GivenAlliance("OW");

        await handler.Handle(
            new UpdateAirlineCommand(1, "BA", null, "British Airways", "GB", "ow"),
            CancellationToken.None);

        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ExistingAirlineWithNoFlights_DeletesAndSaves()
    {
        var handler = new DeleteAirlineCommandHandler(_repo, _flights);
        var airline = Airline.Create("BA", null, "British Airways", "GB");
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(airline);
        _flights.AnyForAirlineAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        await handler.Handle(new DeleteAirlineCommand(1), CancellationToken.None);

        await _repo.Received(1).DeleteAsync(airline, Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_AirlineWithFlights_Throws()
    {
        var handler = new DeleteAirlineCommandHandler(_repo, _flights);
        var airline = Airline.Create("BA", null, "British Airways", "GB");
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(airline);
        _flights.AnyForAirlineAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(new DeleteAirlineCommand(1), CancellationToken.None));
        Assert.Equal(409, ex.StatusCode);

        await _repo.DidNotReceive().DeleteAsync(Arg.Any<Airline>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_NotFound_ReturnsWithoutSaving()
    {
        var handler = new DeleteAirlineCommandHandler(_repo, _flights);
        _repo.GetByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();

        await handler.Handle(new DeleteAirlineCommand(99), CancellationToken.None);

        await _repo.DidNotReceive().DeleteAsync(Arg.Any<Airline>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
