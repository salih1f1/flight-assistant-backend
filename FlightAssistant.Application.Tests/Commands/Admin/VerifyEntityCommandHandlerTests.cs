using FlightAssistant.Application.Commands.Admin.Verify;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin;

public class VerifyEntityCommandHandlerTests
{
    private readonly IVerifiableRepository<Airport> _repo = Substitute.For<IVerifiableRepository<Airport>>();

    // ── Verify ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_ExistingEntity_VerifiesAndSavesAndReturnsTrue()
    {
        var airport = Airport.Create("LHR", null, "Heathrow", "London", "GB", null, null, null);
        _repo.FindByKeyAsync(1, Arg.Any<CancellationToken>()).Returns(airport);
        var handler = new VerifyEntityCommandHandler<Airport>(_repo);

        var result = await handler.Handle(
            new VerifyEntityCommand<Airport>(1, true, "admin@example.com"),
            CancellationToken.None);

        Assert.True(result);
        Assert.True(airport.IsVerified);
        Assert.Equal("admin@example.com", airport.VerifiedBy);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingEntity_Verifies_RecordsManualAsVerificationSource()
    {
        var airport = Airport.Create("LHR", null, "Heathrow", "London", "GB", null, null, null);
        _repo.FindByKeyAsync(1, Arg.Any<CancellationToken>()).Returns(airport);
        var handler = new VerifyEntityCommandHandler<Airport>(_repo);

        await handler.Handle(new VerifyEntityCommand<Airport>(1, true, "admin@example.com"), CancellationToken.None);

        // The manual verify route always records VerificationSources.Manual, never the admin's own
        // id/email or any other value the caller might pass — VerifyEntityCommand carries no source
        // field for exactly that reason.
        Assert.Equal(VerificationSources.Manual, airport.VerificationSource);
    }

    [Fact]
    public async Task Handle_EntityInNeedsAttention_Verify_ClearsVerificationIssue()
    {
        var airport = Airport.Create("LHR", null, "Heathrow", "London", "GB", null, null, null);
        airport.MarkVerificationFailed("Name differs (ourairports)"); // Needs attention state
        _repo.FindByKeyAsync(1, Arg.Any<CancellationToken>()).Returns(airport);
        var handler = new VerifyEntityCommandHandler<Airport>(_repo);

        await handler.Handle(new VerifyEntityCommand<Airport>(1, true, "admin@example.com"), CancellationToken.None);

        Assert.True(airport.IsVerified);
        Assert.Null(airport.VerificationIssue);
    }

    [Fact]
    public async Task Handle_ExistingEntity_UnverifiesAndSavesAndReturnsTrue()
    {
        var airport = Airport.Create("LHR", null, "Heathrow", "London", "GB", null, null, null);
        airport.Verify("admin@example.com", VerificationSources.Manual);
        _repo.FindByKeyAsync(1, Arg.Any<CancellationToken>()).Returns(airport);
        var handler = new VerifyEntityCommandHandler<Airport>(_repo);

        var result = await handler.Handle(
            new VerifyEntityCommand<Airport>(1, false, "admin@example.com"),
            CancellationToken.None);

        Assert.True(result);
        Assert.False(airport.IsVerified);
        Assert.Null(airport.VerifiedBy);
        Assert.Null(airport.VerificationSource);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NotFound_ReturnsFalseWithoutSaving()
    {
        _repo.FindByKeyAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new VerifyEntityCommandHandler<Airport>(_repo);

        var result = await handler.Handle(
            new VerifyEntityCommand<Airport>(99, true, "admin@example.com"),
            CancellationToken.None);

        Assert.False(result);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Country, keyed by code (#331) ────────────────────────────────────────

    private readonly IVerifiableRepository<Country> _countryRepo = Substitute.For<IVerifiableRepository<Country>>();

    [Fact]
    public async Task Handle_CountryFoundByCode_VerifiesAndSavesAndReturnsTrue()
    {
        var country = Country.Create("TR", "Turkey");
        _countryRepo.FindByKeyAsync("TR", Arg.Any<CancellationToken>()).Returns(country);
        var handler = new VerifyEntityCommandHandler<Country>(_countryRepo);

        var result = await handler.Handle(
            new VerifyEntityCommand<Country>("TR", true, "admin@example.com"),
            CancellationToken.None);

        Assert.True(result);
        Assert.True(country.IsVerified);
        Assert.Equal("admin@example.com", country.VerifiedBy);
        Assert.Equal(VerificationSources.Manual, country.VerificationSource);
        await _countryRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_VerifiedCountry_UnverifiesAndSavesAndReturnsTrue()
    {
        var country = Country.Create("TR", "Turkey");
        country.Verify("admin@example.com", VerificationSources.Manual);
        _countryRepo.FindByKeyAsync("TR", Arg.Any<CancellationToken>()).Returns(country);
        var handler = new VerifyEntityCommandHandler<Country>(_countryRepo);

        var result = await handler.Handle(
            new VerifyEntityCommand<Country>("TR", false, "admin@example.com"),
            CancellationToken.None);

        Assert.True(result);
        Assert.False(country.IsVerified);
        Assert.Null(country.VerifiedBy);
        Assert.Null(country.VerifiedAt);
        Assert.Null(country.VerificationSource);
        await _countryRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnknownCountryCode_ReturnsFalseWithoutSaving()
    {
        _countryRepo.FindByKeyAsync("ZZ", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new VerifyEntityCommandHandler<Country>(_countryRepo);

        var result = await handler.Handle(
            new VerifyEntityCommand<Country>("ZZ", true, "admin@example.com"),
            CancellationToken.None);

        Assert.False(result);
        await _countryRepo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
