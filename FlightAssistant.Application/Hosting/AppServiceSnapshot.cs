namespace FlightAssistant.Application.Hosting
{
    /// <param name="SiteState">Azure's own word — "Running", "Stopped". Deliberately separate from
    /// <see cref="AppServiceSnapshot.State"/>, which says whether we could READ the resource: a
    /// stopped site read successfully is State=Ok, SiteState=Stopped, and conflating the two would
    /// make "we could not ask" indistinguishable from "it is not running".</param>
    /// <param name="AlwaysOn">Whether App Service keeps the app loaded. When false the app is
    /// unloaded after roughly twenty minutes idle and IHostedService stops with it, so the
    /// background jobs pause and resume with traffic. Null when unknown — which is NOT the same as
    /// false, and the panel must not treat it as such.</param>
    public sealed record AppServiceSnapshot(
        AppServiceState State,
        string? SiteName,
        string? SiteState,
        string? AvailabilityState,
        bool? Enabled,
        bool? AlwaysOn,
        int? InstanceCount,
        string? Message)
    {
        public static AppServiceSnapshot NotConfigured() => new(
            AppServiceState.NotConfigured, null, null, null, null, null, null,
            "Not running on Azure App Service.");

        public static AppServiceSnapshot NotAuthorized() => new(
            AppServiceState.NotAuthorized, null, null, null, null, null, null,
            "Grant the API's managed identity the Reader role on its resource group.");

        public static AppServiceSnapshot Unavailable(string message) => new(
            AppServiceState.Unavailable, null, null, null, null, null, null, message);
    }
}
