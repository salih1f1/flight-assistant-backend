using FlightAssistant.Infrastructure.ExternalServices.Azure;
using Xunit;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Azure;

/// <summary>
/// Parsing the subscription id out of App Service's <c>WEBSITE_OWNER_NAME</c> (#523). The API runs
/// inside the subscription it reports on, so requiring <c>AzureCost:SubscriptionId</c> duplicated
/// something the platform already knows.
///
/// <para>
/// Every malformed case answers <c>null</c> rather than a best guess. A wrong subscription id is
/// not a smaller error than a missing one: it sends a request to a subscription we were never
/// asked about, and the panel would report <c>NotAuthorized</c> or someone else's spend rather
/// than saying it does not know which subscription to use.
/// </para>
/// </summary>
public class AzureSubscriptionTests
{
    // The documented App Service format: {subscriptionId}+{resourceGroup}-{region}webspace.
    [Fact]
    public void ReadsTheSubscriptionIdFromARealWebsiteOwnerName()
    {
        var owner = "a39b9c33-675b-40ca-84fc-6e548ef6d6c5+flight-assistant-dev-WestEuropewebspace";

        Assert.Equal("a39b9c33-675b-40ca-84fc-6e548ef6d6c5", AzureSubscription.FromWebsiteOwnerName(owner));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentValueIsNull(string? owner)
    {
        Assert.Null(AzureSubscription.FromWebsiteOwnerName(owner));
    }

    // No separator means this is not the shape we think it is. Treating the whole string as an id
    // would send whatever it is to Azure as a subscription.
    [Fact]
    public void AValueWithNoSeparatorIsNull()
    {
        Assert.Null(AzureSubscription.FromWebsiteOwnerName("flight-assistant-dev-WestEuropewebspace"));
    }

    // The guard that matters most: a first segment that is not a GUID is not a subscription id,
    // whatever else it may be.
    [Fact]
    public void AFirstSegmentThatIsNotAGuidIsNull()
    {
        Assert.Null(AzureSubscription.FromWebsiteOwnerName("not-a-subscription+flight-assistant-dev-WestEuropewebspace"));
    }

    [Fact]
    public void AnEmptyFirstSegmentIsNull()
    {
        Assert.Null(AzureSubscription.FromWebsiteOwnerName("+flight-assistant-dev-WestEuropewebspace"));
    }

    // Azure has used both casings for resource ids over the years; a GUID is a GUID.
    [Fact]
    public void AnUppercaseGuidIsAccepted()
    {
        var owner = "A39B9C33-675B-40CA-84FC-6E548EF6D6C5+rg-Westeuropewebspace";

        Assert.Equal("A39B9C33-675B-40CA-84FC-6E548EF6D6C5", AzureSubscription.FromWebsiteOwnerName(owner));
    }
}
