using FlightAssistant.Application.Commands.Travelers;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Travelers.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Travelers;

public class UpdateTravelerProfileCommandHandlerTests
{
    private readonly ITravelerRepository _travelers = Substitute.For<ITravelerRepository>();
    private readonly UpdateTravelerProfileCommandHandler _handler;

    public UpdateTravelerProfileCommandHandlerTests()
    {
        _handler = new UpdateTravelerProfileCommandHandler(_travelers);
    }

    [Fact]
    public async Task UpdatesDomainOwnedCountries_AndSaves()
    {
        var travelerId = Guid.NewGuid();
        var traveler = Traveler.Register("carol@example.com", "Carol", "US");
        _travelers.GetByIdAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);

        var cmd = new UpdateTravelerProfileCommand(travelerId, "GB", "DE");
        await _handler.Handle(cmd, CancellationToken.None);

        Assert.Equal("GB", traveler.NationalityCountryCode);
        Assert.Equal("DE", traveler.ResidenceCountryCode);
        await _travelers.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DoesNotTouchAzureOwnedIdentityFields()
    {
        var travelerId = Guid.NewGuid();
        var traveler = Traveler.Register("carol@example.com", "Carol", "US");
        _travelers.GetByIdAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);

        var cmd = new UpdateTravelerProfileCommand(travelerId, "GB", "DE");
        await _handler.Handle(cmd, CancellationToken.None);

        // Display name and country come from Azure — the profile update must leave them alone.
        Assert.Equal("Carol", traveler.DisplayName);
        Assert.Equal("US", traveler.CountryCode);
    }

    [Fact]
    public async Task TravelerNotFound_Throws()
    {
        _travelers.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _handler.Handle(new UpdateTravelerProfileCommand(Guid.NewGuid(), null, null), CancellationToken.None));
        Assert.Equal(404, ex.StatusCode);
    }
}
