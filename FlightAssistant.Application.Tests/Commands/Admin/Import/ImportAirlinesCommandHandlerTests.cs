using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportAirlinesCommandHandlerTests
{
    private readonly IAirlineRepository _repo = Substitute.For<IAirlineRepository>();
    private readonly IAdminContext _adminContext = new AdminContext { ActorId = "importing-admin" };

    private ImportAirlinesCommandHandler Handler() => new(_repo, _adminContext);

    [Fact]
    public async Task Handle_NewAirline_InsertsAndReturnsInsertedCount()
    {
        _repo.FindByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _repo.FindByIcaoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
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
        var handler = Handler();
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
        var handler = Handler();
        var dto = new AirlineImportDto(null, "BAW", "British Airways Updated", "GB", null);

        var result = await handler.Handle(new ImportAirlinesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Equal("British Airways Updated", existing.Name);
    }

    // ── #362: import verifies rows (spec §7 reverses the #355-era "stays unverified" rule) ──

    [Fact]
    public async Task Handle_ImportedAirline_NoVerificationValues_EndsVerifiedAsImportByActor()
    {
        _repo.FindByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _repo.FindByIcaoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        Airline? added = null;
        await _repo.AddAsync(Arg.Do<Airline>(a => added = a), Arg.Any<CancellationToken>());
        var handler = Handler();
        var dto = new AirlineImportDto("BA", null, "British Airways", "GB", null);

        await handler.Handle(new ImportAirlinesCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.True(added!.IsVerified);
        Assert.Equal("importing-admin", added.VerifiedBy);
        Assert.Equal(VerificationSources.Import, added.VerificationSource);
    }

    [Fact]
    public async Task Handle_NewAirline_AllVerificationValuesPresent_KeepsThemAsIs()
    {
        _repo.FindByIataAsync("BA", Arg.Any<CancellationToken>()).ReturnsNull();
        _repo.FindByIcaoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        Airline? added = null;
        await _repo.AddAsync(Arg.Do<Airline>(a => added = a), Arg.Any<CancellationToken>());
        var handler = Handler();
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var dto = new AirlineImportDto("BA", "BAW", "British Airways", "GB", "OW",
            verifiedAt, "prior-admin", "manual");

        await handler.Handle(new ImportAirlinesCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.Equal(verifiedAt, added!.VerifiedAt);
        Assert.Equal("prior-admin", added.VerifiedBy);
        Assert.Equal("manual", added.VerificationSource);
    }

    [Fact]
    public async Task Handle_PartiallyFilledVerificationRow_IsRejected_WhileOtherRowInSameFileImports()
    {
        _repo.FindByIataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        _repo.FindByIcaoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var partial = new AirlineImportDto("BA", "BAW", "British Airways", "GB", "OW",
            VerifiedBy: "prior-admin"); // VerifiedAt/VerificationSource missing
        var valid = new AirlineImportDto("AA", "AAL", "American Airlines", "US", "OW");

        var result = await handler.Handle(new ImportAirlinesCommand([partial, valid]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Single(result.Errors);
        Assert.Contains("BA", result.Errors[0]);
        await _repo.DidNotReceive().AddAsync(
            Arg.Is<Airline>(a => a.Iata == "BA"), Arg.Any<CancellationToken>());
        await _repo.Received(1).AddAsync(
            Arg.Is<Airline>(a => a.Iata == "AA"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingAirlineInNeedsAttention_IsVerifiedAndIssueCleared()
    {
        var existing = Airline.Create("BA", null, "Old Name", "GB", null);
        existing.MarkVerificationFailed("stale seed data");
        _repo.FindByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = Handler();
        var dto = new AirlineImportDto("BA", "BAW", "British Airways", "GB", "OW");

        await handler.Handle(new ImportAirlinesCommand([dto]), CancellationToken.None);

        Assert.True(existing.IsVerified);
        Assert.Null(existing.VerificationIssue);
        Assert.Equal(VerificationSources.Import, existing.VerificationSource);
    }
}
