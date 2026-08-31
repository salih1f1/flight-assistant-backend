using FlightAssistant.Application.Commands.Admin.Verify;
using FlightAssistant.Application.Interfaces.Repositories;
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
        _repo.FindByIdAsync(1, Arg.Any<CancellationToken>()).Returns(airport);
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
    public async Task Handle_ExistingEntity_UnverifiesAndSavesAndReturnsTrue()
    {
        var airport = Airport.Create("LHR", null, "Heathrow", "London", "GB", null, null, null);
        airport.Verify("admin@example.com");
        _repo.FindByIdAsync(1, Arg.Any<CancellationToken>()).Returns(airport);
        var handler = new VerifyEntityCommandHandler<Airport>(_repo);

        var result = await handler.Handle(
            new VerifyEntityCommand<Airport>(1, false, "admin@example.com"),
            CancellationToken.None);

        Assert.True(result);
        Assert.False(airport.IsVerified);
        Assert.Null(airport.VerifiedBy);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NotFound_ReturnsFalseWithoutSaving()
    {
        _repo.FindByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new VerifyEntityCommandHandler<Airport>(_repo);

        var result = await handler.Handle(
            new VerifyEntityCommand<Airport>(99, true, "admin@example.com"),
            CancellationToken.None);

        Assert.False(result);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
