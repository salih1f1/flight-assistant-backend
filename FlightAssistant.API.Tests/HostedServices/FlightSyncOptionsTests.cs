using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.FlightSync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.Tests.HostedServices;

public class FlightSyncOptionsTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddFlightSync();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        using var provider = Build(new());
        var options = provider.GetRequiredService<IOptions<FlightSyncOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), options.LoopInterval);
        Assert.Equal(TimeSpan.FromMinutes(2), options.NearDepartureInterval);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Interval);
        Assert.Equal(TimeSpan.FromDays(1), options.ScheduleCheckInterval);
        Assert.Equal(TimeSpan.FromHours(4), options.FastWindowBeforeDeparture);
        Assert.Equal(TimeSpan.FromMinutes(30), options.StopAfterArrival);
        Assert.Equal(TimeSpan.FromDays(1), options.StopWithoutArrivalAfter);
    }

    [Fact]
    public void IFlightSyncOptions_ReflectsTheBoundSection()
    {
        using var provider = Build(new()
        {
            ["FlightSync:NearDepartureInterval"] = "00:03:00",
            ["FlightSync:Interval"] = "00:07:00",
            ["FlightSync:ScheduleCheckInterval"] = "06:00:00",
            ["FlightSync:FastWindowBeforeDeparture"] = "03:00:00",
            ["FlightSync:StopAfterArrival"] = "00:20:00",
            ["FlightSync:StopWithoutArrivalAfter"] = "12:00:00",
        });
        var options = provider.GetRequiredService<IFlightSyncOptions>();

        Assert.Equal(TimeSpan.FromMinutes(3), options.NearDepartureInterval);
        Assert.Equal(TimeSpan.FromMinutes(7), options.Interval);
        Assert.Equal(TimeSpan.FromHours(6), options.ScheduleCheckInterval);
        Assert.Equal(TimeSpan.FromHours(3), options.FastWindowBeforeDeparture);
        Assert.Equal(TimeSpan.FromMinutes(20), options.StopAfterArrival);
        Assert.Equal(TimeSpan.FromHours(12), options.StopWithoutArrivalAfter);
    }

    [Theory]
    [InlineData("LoopInterval", "00:00:00")]
    [InlineData("NearDepartureInterval", "00:00:00")]
    [InlineData("Interval", "00:00:00")]
    [InlineData("ScheduleCheckInterval", "00:00:00")]
    [InlineData("FastWindowBeforeDeparture", "00:00:00")]
    [InlineData("StopAfterArrival", "-00:01:00")]
    [InlineData("StopWithoutArrivalAfter", "00:00:00")]
    public void AnOutOfRangeValue_FailsOnStart(string key, string value)
    {
        using var provider = Build(new() { [$"FlightSync:{key}"] = value });

        // IStartupValidator is what the host runs at startup; it exists only because of ValidateOnStart.
        var ex = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains(key, ex.Message);
    }
}
