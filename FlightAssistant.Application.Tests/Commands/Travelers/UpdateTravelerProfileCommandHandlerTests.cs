using FlightAssistant.Application.Commands.Travelers;
using FlightAssistant.Application.Interfaces.Repositories;
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
    public async Task UpdatesProfile_AndSaves()
    {
        var travelerId = Guid.NewGuid();
        var traveler = Traveler.Register("carol@example.com");
        _travelers.GetByIdAsync(travelerId, Arg.Any<CancellationToken>()).Returns(traveler);

        var cmd = new UpdateTravelerProfileCommand(travelerId, "Carol", "GB", "GB", "DE");
        await _handler.Handle(cmd, CancellationToken.None);

        Assert.Equal("Carol", traveler.DisplayName);
        Assert.Equal("GB", traveler.CountryCode);
        Assert.Equal("GB", traveler.NationalityCountryCode);
        Assert.Equal("DE", traveler.ResidenceCountryCode);
        await _travelers.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TravelerNotFound_Throws()
    {
        _travelers.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ReturnsNull();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _handler.Handle(new UpdateTravelerProfileCommand(Guid.NewGuid(), "X", null, null, null), CancellationToken.None));
    }
}
