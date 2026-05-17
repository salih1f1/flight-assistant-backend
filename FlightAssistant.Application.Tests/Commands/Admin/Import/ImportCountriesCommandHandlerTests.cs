using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportCountriesCommandHandlerTests
{
    private readonly ICountryRepository _repo = Substitute.For<ICountryRepository>();

    [Fact]
    public async Task Handle_NewCountry_InsertsAndReturnsInsertedCount()
    {
        _repo.FindByCodeAsync("GB", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new ImportCountriesCommandHandler(_repo);
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
        var handler = new ImportCountriesCommandHandler(_repo);
        var dto = new CountryImportDto("GB", "United Kingdom", "GBR", 826, null, null, null, null, null);

        var result = await handler.Handle(new ImportCountriesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddAsync(Arg.Any<Country>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal("United Kingdom", existing.Name);
    }

    [Fact]
    public async Task Handle_ImportedCountry_IsNotVerified()
    {
        _repo.FindByCodeAsync("GB", Arg.Any<CancellationToken>()).ReturnsNull();
        Country? added = null;
        await _repo.AddAsync(Arg.Do<Country>(c => added = c), Arg.Any<CancellationToken>());
        var handler = new ImportCountriesCommandHandler(_repo);
        var dto = new CountryImportDto("GB", "United Kingdom", null, null, null, null, null, null, null);

        await handler.Handle(new ImportCountriesCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.False(added!.IsVerified);
    }
}
