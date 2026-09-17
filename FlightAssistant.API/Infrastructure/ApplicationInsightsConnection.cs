namespace FlightAssistant.API.Infrastructure
{
    /// <summary>
    /// Resolves the Application Insights connection string (#440). The repo's documented key is
    /// <c>ApplicationInsights:ConnectionString</c>. App Service instead sets the environment variable
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>. The environment-variable provider maps only
    /// <c>__</c> to <c>:</c>, so that variable never appears under the documented key. Without this
    /// fallback, telemetry stayed off in every App Service environment that relied on the platform
    /// setting.
    /// </summary>
    public static class ApplicationInsightsConnection
    {
        public const string ConfigurationKey = "ApplicationInsights:ConnectionString";
        public const string PlatformKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

        /// <summary>The documented key when it has a value, otherwise the platform key, otherwise null.</summary>
        public static string? Resolve(IConfiguration configuration)
        {
            var documented = configuration[ConfigurationKey];
            if (!string.IsNullOrWhiteSpace(documented)) return documented;

            var platform = configuration[PlatformKey];
            return string.IsNullOrWhiteSpace(platform) ? null : platform;
        }
    }
}
