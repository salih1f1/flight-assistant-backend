namespace FlightAssistant.Infrastructure.ExternalServices.Azure
{
    /// <summary>
    /// Works out which subscription the API is running in, from App Service's own
    /// <c>WEBSITE_OWNER_NAME</c> (#523).
    ///
    /// <para>
    /// The cost panel reports on the subscription the API is hosted in, and App Service already
    /// says which that is — so requiring <c>AzureCost:SubscriptionId</c> duplicated something the
    /// platform knows, at the cost of a deployment step that would be forgotten and a
    /// "not configured" state reporting a requirement we invented. The explicit setting still
    /// wins where it is set, which keeps the one case that genuinely needs it: pointing the panel
    /// at a subscription other than the one hosting the API.
    /// </para>
    ///
    /// <para>
    /// Every malformed value answers <c>null</c> rather than a best guess. A wrong subscription id
    /// is not a smaller error than a missing one — it queries a subscription nobody asked about,
    /// and the panel would report <c>NotAuthorized</c>, or somebody else's spend, instead of
    /// saying plainly that it does not know which subscription to use.
    /// </para>
    /// </summary>
    public static class AzureSubscription
    {
        /// <summary>
        /// The subscription id from a <c>WEBSITE_OWNER_NAME</c>, or <c>null</c> when the value is
        /// absent or is not the shape App Service documents —
        /// <c>{subscriptionId}+{resourceGroup}-{region}webspace</c>.
        /// </summary>
        public static string? FromWebsiteOwnerName(string? websiteOwnerName)
        {
            if (string.IsNullOrWhiteSpace(websiteOwnerName)) return null;

            var separator = websiteOwnerName.IndexOf('+');
            if (separator <= 0) return null;

            var candidate = websiteOwnerName[..separator];

            // A GUID is the whole test. Anything else in that position means the value is not the
            // shape we think it is, and guessing from it would be worse than not answering.
            return Guid.TryParse(candidate, out _) ? candidate : null;
        }
    }
}
