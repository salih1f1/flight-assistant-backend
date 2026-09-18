using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportAirportsCommandHandlerTests
{
    private readonly IAirportRepository _repo = Substitute.For<IAirportRepository>();
    private readonly IAdminContext _adminContext = new AdminContext { ActorId = "importing-admin" };

    private readonly ICountryRepository _countries = Substitute.For<ICountryRepository>();

    public ImportAirportsCommandHandlerTests()
    {
        // The rows below are GB and US, so the #455 country guard has countries to find.
        _countries.FindByCodeAsync("GB", Arg.Any<CancellationToken>())
            .Returns(Country.Create("GB", "United Kingdom", "GBR", 826));
        _countries.FindByCodeAsync("US", Arg.Any<CancellationToken>())
            .Returns(Country.Create("US", "United States", "USA", 840));
    }

    private ImportAirportsCommandHandler Handler() => new(_repo, _adminContext, _countries);

    [Fact]
    public async Task Handle_NewAirport_InsertsAndReturnsInsertedCount()
    {
        _repo.FindByIataAsync("LHR", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var dto = new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", 51.477, -0.461, 83);

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
        var handler = Handler();
        var dto = new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", 51.477, -0.461, 83);

        var result = await handler.Handle(new ImportAirportsCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddAsync(Arg.Any<Airport>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── #362: import verifies rows (spec §7 reverses the #355-era "stays unverified" rule) ──

    [Fact]
    public async Task Handle_ImportedAirport_NoVerificationValues_EndsVerifiedAsImportByActor()
    {
        _repo.FindByIataAsync("LHR", Arg.Any<CancellationToken>()).ReturnsNull();
        Airport? added = null;
        await _repo.AddAsync(Arg.Do<Airport>(a => added = a), Arg.Any<CancellationToken>());
        var handler = Handler();
        var dto = new AirportImportDto("LHR", null, "Heathrow", "London", "GB", null, null, null);

        await handler.Handle(new ImportAirportsCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.True(added!.IsVerified);
        Assert.Equal("importing-admin", added.VerifiedBy);
        Assert.Equal(VerificationSources.Import, added.VerificationSource);
    }

    [Fact]
    public async Task Handle_NewAirport_AllVerificationValuesPresent_KeepsThemAsIs()
    {
        _repo.FindByIataAsync("LHR", Arg.Any<CancellationToken>()).ReturnsNull();
        Airport? added = null;
        await _repo.AddAsync(Arg.Do<Airport>(a => added = a), Arg.Any<CancellationToken>());
        var handler = Handler();
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var dto = new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", 51.477, -0.461, 83,
            verifiedAt, "prior-admin", "manual");

        await handler.Handle(new ImportAirportsCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.Equal(verifiedAt, added!.VerifiedAt);
        Assert.Equal("prior-admin", added.VerifiedBy);
        Assert.Equal("manual", added.VerificationSource);
    }

    [Fact]
    public async Task Handle_PartiallyFilledVerificationRow_IsRejected_WhileOtherRowInSameFileImports()
    {
        _repo.FindByIataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var partial = new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", 51.477, -0.461, 83,
            VerifiedBy: "prior-admin"); // VerifiedAt/VerificationSource missing
        var valid = new AirportImportDto("JFK", null, "JFK Airport", "New York", "US", null, null, null);

        var result = await handler.Handle(new ImportAirportsCommand([partial, valid]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Single(result.Errors);
        Assert.Contains("LHR", result.Errors[0]);
        await _repo.DidNotReceive().AddAsync(
            Arg.Is<Airport>(a => a.Iata == "LHR"), Arg.Any<CancellationToken>());
        await _repo.Received(1).AddAsync(
            Arg.Is<Airport>(a => a.Iata == "JFK"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingAirportInNeedsAttention_IsVerifiedAndIssueCleared()
    {
        var existing = Airport.Create("LHR", null, "Old Name", "London", "GB");
        existing.MarkVerificationFailed("stale seed data");
        _repo.FindByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = Handler();
        var dto = new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", 51.477, -0.461, 83);

        await handler.Handle(new ImportAirportsCommand([dto]), CancellationToken.None);

        Assert.True(existing.IsVerified);
        Assert.Null(existing.VerificationIssue);
        Assert.Equal(VerificationSources.Import, existing.VerificationSource);
    }

    [Fact]
    public async Task Handle_CountryCodeThatIsNoCountry_FailsThatRowOnlyAndStillImportsTheRest()
    {
        _repo.FindByIataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        _repo.FindByIcaoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        _countries.FindByCodeAsync("ZZ", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var bad = new AirportImportDto("ZZZ", null, "Nowhere", "Nowhere", "ZZ", null, null, null);
        var good = new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", null, null, null);

        var result = await handler.Handle(new ImportAirportsCommand([bad, good]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        var error = Assert.Single(result.Errors);
        Assert.Contains("ZZ", error);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
