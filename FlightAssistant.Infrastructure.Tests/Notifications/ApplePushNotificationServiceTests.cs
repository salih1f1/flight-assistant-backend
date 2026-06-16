using dotAPNS;
using FlightAssistant.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.Notifications;

public class ApplePushNotificationServiceTests
{
    private static ApplePushNotificationService CreateService(IApnsClient client) =>
        new(client, NullLogger<ApplePushNotificationService>.Instance);

    [Theory]
    [InlineData(ApnsResponseReason.BadDeviceToken)]
    [InlineData(ApnsResponseReason.Unregistered)]
    [InlineData(ApnsResponseReason.DeviceTokenNotForTopic)]
    public async Task SendAsync_PermanentReason_ReturnsTokenAsInvalid(ApnsResponseReason reason)
    {
        var client = Substitute.For<IApnsClient>();
        client.SendAsync(Arg.Any<ApplePush>(), Arg.Any<CancellationToken>())
            .Returns(ApnsResponse.Error(reason, reason.ToString()));

        var service = CreateService(client);

        var invalid = await service.SendAsync(new[] { "bad-token-123456" }, "t", "b");

        Assert.Equal(new[] { "bad-token-123456" }, invalid);
    }

    [Fact]
    public async Task SendAsync_Successful_ReturnsNoInvalidTokens()
    {
        var client = Substitute.For<IApnsClient>();
        client.SendAsync(Arg.Any<ApplePush>(), Arg.Any<CancellationToken>())
            .Returns(ApnsResponse.Successful());

        var service = CreateService(client);

        var invalid = await service.SendAsync(new[] { "good-token-123456" }, "t", "b");

        Assert.Empty(invalid);
    }

    [Fact]
    public async Task SendAsync_MixedTokens_ReturnsOnlyPermanentlyInvalid()
    {
        var client = Substitute.For<IApnsClient>();
        client.SendAsync(Arg.Any<ApplePush>(), Arg.Any<CancellationToken>())
            .Returns(
                ApnsResponse.Successful(),
                ApnsResponse.Error(ApnsResponseReason.Unregistered, "Unregistered"));

        var service = CreateService(client);

        var invalid = await service.SendAsync(new[] { "good-token-123456", "bad-token-123456" }, "t", "b");

        Assert.Equal(new[] { "bad-token-123456" }, invalid);
    }
}
