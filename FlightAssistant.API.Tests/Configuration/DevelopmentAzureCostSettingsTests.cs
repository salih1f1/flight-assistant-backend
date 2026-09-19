using FlightAssistant.Infrastructure.ExternalServices.Azure;
using Microsoft.Extensions.Configuration;

namespace FlightAssistant.API.Tests.Configuration;

/// <summary>
/// The Azure spend panel needs a subscription to report on, and a local run has no
/// <c>WEBSITE_OWNER_NAME</c> to fall back to (#523) — App Service injects that, nothing else does.
/// So <c>appsettings.Development.json</c> has to carry the id itself (#533), or every local run of
/// the dashboard shows a permanently dead card.
///
/// Reads the real settings file rather than an in-memory stand-in, because the file IS the subject:
/// an in-memory copy would pass with the key deleted from disk. It reaches the test through the
/// build — the Web SDK copies every <c>appsettings.*.json</c> to the output of anything referencing
/// the API project, which is why <see cref="AppContext.BaseDirectory"/> finds it.
///
/// Whether an explicit setting beats the fallback is <c>AzureCostDependencyInjectionTests</c>'s
/// question, not this one. This asks only that the Development file names a subscription.
/// </summary>
public class DevelopmentAzureCostSettingsTests
{
    private static AzureCostOptions DevelopmentAzureCost()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json"), optional: false)
            .Build();

        var options = new AzureCostOptions();
        config.GetSection(AzureCostOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void ASubscriptionIsConfigured()
    {
        Assert.True(
            DevelopmentAzureCost().IsConfigured,
            "appsettings.Development.json must set AzureCost:SubscriptionId — without it a local " +
            "run reports the Azure spend card as 'Not configured' forever.");
    }

    /// <summary>
    /// A placeholder left behind by a careless edit — "TODO", an empty string, the word "subscription"
    /// — satisfies <see cref="AzureCostOptions.IsConfigured"/>, which only asks for non-blank text.
    /// It would then be sent to Cost Management as a subscription id and come back as an opaque
    /// <c>Unavailable</c>, so the shape is pinned here rather than the value: pinning the literal id
    /// would only mirror the file back at itself.
    /// </summary>
    [Fact]
    public void TheConfiguredSubscriptionIsAGuid()
    {
        var subscriptionId = DevelopmentAzureCost().SubscriptionId;

        Assert.True(
            Guid.TryParse(subscriptionId, out _),
            $"AzureCost:SubscriptionId must be a subscription GUID, but was '{subscriptionId}'.");
    }
}
