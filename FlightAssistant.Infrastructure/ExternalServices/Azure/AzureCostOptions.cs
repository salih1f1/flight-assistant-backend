namespace FlightAssistant.Infrastructure.ExternalServices.Azure
{
    /// <summary>
    /// Azure Cost Management settings. Both are optional: absent <see cref="SubscriptionId"/> is
    /// the normal state for a local developer, and the panel says "not configured" rather than
    /// failing — the same shape as <c>GitHubOptions.Token</c> and <c>AeroDataBoxOptions.ApiKey</c>.
    /// </summary>
    public sealed class AzureCostOptions
    {
        // DEVIATION FROM THE SPEC, deliberate. The spec wrote these as `Azure:SubscriptionId` and
        // `Azure:BudgetName`, but appsettings.json already has an `Azure` section holding the B2C
        // sign-in policy, and mixing cost settings into it would put two unrelated concerns under
        // one key. A separate section keeps them apart, so the keys are `AzureCost:SubscriptionId`
        // and `AzureCost:BudgetName`. Anything quoting the spec's spelling — the endpoint
        // description, the NotConfigured message — must use these instead.
        public const string SectionName = "AzureCost";

        /// <summary>The subscription whose spend the panel reports.</summary>
        public string? SubscriptionId { get; set; }

        /// <summary>Which budget to use when the subscription has more than one. Unset takes the
        /// first returned — fine for one budget, and named explicitly when there are several.</summary>
        public string? BudgetName { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(SubscriptionId);
    }
}
