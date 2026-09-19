namespace FlightAssistant.Application.Hosting
{
    /// <summary>
    /// What the App Service panel can honestly say. Derived once, in the provider, so the panel
    /// renders a state rather than inferring one from nulls — the same split the data-source
    /// inventory uses.
    /// </summary>
    public enum AppServiceState
    {
        /// <summary>The resource was read.</summary>
        Ok,

        /// <summary>Not running on App Service — no <c>WEBSITE_SITE_NAME</c>. A developer machine,
        /// which is not a fault.</summary>
        NotConfigured,

        /// <summary>401/403 — the managed identity lacks Reader on the resource group.</summary>
        NotAuthorized,

        /// <summary>Anything else: timeout, 5xx, a body we cannot read.</summary>
        Unavailable,
    }
}
