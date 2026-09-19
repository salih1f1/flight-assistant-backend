namespace FlightAssistant.Application.Usage
{
    /// <summary>
    /// What the Azure cost panel can honestly say. Derived once, in the provider, so the panel
    /// renders a state rather than inferring one from nulls — the same split the data-source
    /// inventory uses.
    /// </summary>
    public enum AzureBudgetState
    {
        /// <summary>Budget and spend both read.</summary>
        Ok,

        /// <summary>Cost Management answered, but no budget is defined on the subscription.</summary>
        NoBudget,

        /// <summary>No <c>AzureCost:SubscriptionId</c>. A local developer has none; not a fault.</summary>
        NotConfigured,

        /// <summary>401/403 — the managed identity lacks Cost Management Reader.</summary>
        NotAuthorized,

        /// <summary>Anything else: timeout, 5xx, a body we cannot read.</summary>
        Unavailable,
    }
}
