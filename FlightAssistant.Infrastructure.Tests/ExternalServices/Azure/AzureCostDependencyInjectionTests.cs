using FlightAssistant.Application;
using FlightAssistant.Infrastructure;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.ExternalServices.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Azure;

/// <summary>
/// The <c>WEBSITE_OWNER_NAME</c> fallback as it is actually wired (#523), through the real
/// <c>AddInfrastructure</c> rather than by calling the parser directly —
/// <see cref="AzureSubscriptionTests"/> covers the parsing itself. What these pin is the wiring:
/// that the fallback runs at all, and that an explicit setting still beats it.
/// </summary>
public class AzureCostDependencyInjectionTests
{
    private const string Owner = "a39b9c33-675b-40ca-84fc-6e548ef6d6c5+flight-assistant-dev-WestEuropewebspace";
    private const string SubscriptionInOwner = "a39b9c33-675b-40ca-84fc-6e548ef6d6c5";

    private static AzureCostOptions Resolve(IDictionary<string, string?> settings)
    {
        // AeroApi:ApiKey is [Required] and validated on start, so the container needs one to build
        // — it has nothing to do with what these tests assert.
        settings["AeroApi:ApiKey"] = "dummy-test-key";

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddAppCaching(config);
        // isDevelopment: true skips the unrelated Apns ValidateOnStart check.
        services.AddInfrastructure(config, isDevelopment: true);
        services.AddApplication();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AzureCostOptions>>().Value;
    }

    [Fact]
    public void TheSubscriptionIsTakenFromWebsiteOwnerName_WhenNoSettingIsGiven()
    {
        var options = Resolve(new Dictionary<string, string?> { ["WEBSITE_OWNER_NAME"] = Owner });

        Assert.Equal(SubscriptionInOwner, options.SubscriptionId);
        Assert.True(options.IsConfigured);
    }

    /// <summary>
    /// The explicit setting is the override that keeps the one case the fallback cannot serve:
    /// reporting on a subscription other than the one hosting the API.
    /// </summary>
    [Fact]
    public void AnExplicitSettingWinsOverWebsiteOwnerName()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["WEBSITE_OWNER_NAME"] = Owner,
            ["AzureCost:SubscriptionId"] = "11111111-2222-3333-4444-555555555555",
        });

        Assert.Equal("11111111-2222-3333-4444-555555555555", options.SubscriptionId);
    }

    /// <summary>
    /// A developer machine has no <c>WEBSITE_OWNER_NAME</c> and no setting, and must still start —
    /// the provider answers NotConfigured rather than failing, which is the honest answer there.
    /// </summary>
    [Fact]
    public void NeitherSourcePresent_LeavesItUnconfigured()
    {
        var options = Resolve(new Dictionary<string, string?>());

        Assert.Null(options.SubscriptionId);
        Assert.False(options.IsConfigured);
    }

    /// <summary>
    /// A malformed value must not be passed through as if it were an id: querying a subscription
    /// nobody asked about would report NotAuthorized, or somebody else's spend, rather than saying
    /// plainly that we do not know which subscription to use.
    /// </summary>
    [Fact]
    public void AMalformedWebsiteOwnerName_LeavesItUnconfigured()
    {
        var options = Resolve(new Dictionary<string, string?> { ["WEBSITE_OWNER_NAME"] = "not-a-subscription+rg-webspace" });

        Assert.Null(options.SubscriptionId);
        Assert.False(options.IsConfigured);
    }
}
