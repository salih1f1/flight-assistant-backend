using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportCountriesCommandHandlerTests
{
    private readonly ICountryRepository _repo = Substitute.For<ICountryRepository>();
    private readonly IAdminContext _adminContext = new AdminContext { ActorId = "importing-admin" };

    private ImportCountriesCommandHandler Handler() => new(_repo, _adminContext);

    [Fact]
    public async Task Handle_NewCountry_InsertsAndReturnsInsertedCount()
    {
        _repo.FindByCodeAsync("GB", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var dto = new CountryImportDto("GB", "United Kingdom", "GBR", 826, "Europe", "Northern Europe", false, false, true);

        var result = await handler.Handle(new ImportCountriesCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingCountry_UpdatesAndReturnsUpdatedCount()
    {
        var existing = Country.Create("GB", "Old Name");
        _repo.FindByCodeAsync("GB", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = Handler();
        var dto = new CountryImportDto("GB", "United Kingdom", "GBR", 826, null, null, null, null, null);

        var result = await handler.Handle(new ImportCountriesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal("United Kingdom", existing.Name);
    }

    // ── #362: import verifies rows (spec §7 reverses the #355-era "stays unverified" rule) ──

    [Fact]
    public async Task Handle_ImportedCountry_NoVerificationValues_EndsVerifiedAsImportByActor()
    {
        _repo.FindByCodeAsync("GB", Arg.Any<CancellationToken>()).ReturnsNull();
        Country? added = null;
        await _repo.AddAsync(Arg.Do<Country>(c => added = c), Arg.Any<CancellationToken>());
        var handler = Handler();
        var dto = new CountryImportDto("GB", "United Kingdom", null, null, null, null, null, null, null);

        await handler.Handle(new ImportCountriesCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.True(added!.IsVerified);
        Assert.Equal("importing-admin", added.VerifiedBy);
        Assert.Equal(VerificationSources.Import, added.VerificationSource);
    }

    [Fact]
    public async Task Handle_NewCountry_AllVerificationValuesPresent_KeepsThemAsIs()
    {
        _repo.FindByCodeAsync("GB", Arg.Any<CancellationToken>()).ReturnsNull();
        Country? added = null;
        await _repo.AddAsync(Arg.Do<Country>(c => added = c), Arg.Any<CancellationToken>());
        var handler = Handler();
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var dto = new CountryImportDto("GB", "United Kingdom", null, null, null, null, null, null, null,
            verifiedAt, "prior-admin", "manual");

        await handler.Handle(new ImportCountriesCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.Equal(verifiedAt, added!.VerifiedAt);
        Assert.Equal("prior-admin", added.VerifiedBy);
        Assert.Equal("manual", added.VerificationSource);
    }

    [Fact]
    public async Task Handle_PartiallyFilledVerificationRow_IsRejected_WhileOtherRowInSameFileImports()
    {
        _repo.FindByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var partial = new CountryImportDto("GB", "United Kingdom", null, null, null, null, null, null, null,
            VerifiedBy: "prior-admin"); // VerifiedAt/VerificationSource missing
        var valid = new CountryImportDto("FR", "France", null, null, null, null, null, null, null);

        var result = await handler.Handle(new ImportCountriesCommand([partial, valid]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Single(result.Errors);
        Assert.Contains("GB", result.Errors[0]);
        await _repo.DidNotReceive().AddAsync(
            Arg.Is<Country>(c => c.Code == "GB"), Arg.Any<CancellationToken>());
        await _repo.Received(1).AddAsync(
            Arg.Is<Country>(c => c.Code == "FR"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingCountryInNeedsAttention_IsVerifiedAndIssueCleared()
    {
        var existing = Country.Create("GB", "Old Name");
        existing.MarkVerificationFailed("stale seed data");
        _repo.FindByCodeAsync("GB", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = Handler();
        var dto = new CountryImportDto("GB", "United Kingdom", null, null, null, null, null, null, null);

        await handler.Handle(new ImportCountriesCommand([dto]), CancellationToken.None);

        Assert.True(existing.IsVerified);
        Assert.Null(existing.VerificationIssue);
        Assert.Equal(VerificationSources.Import, existing.VerificationSource);
    }
}
