using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using FlightAssistant.Infrastructure.ExternalServices.GitHub;
using FlightAssistant.Infrastructure.ExternalServices.Verification;

namespace FlightAssistant.Infrastructure.DataSources.Probes
{
    /// <summary>
    /// Reads the account-usage report. FlightAware bills that report at $0.00, so this is the rare
    /// case where the probe can check the credential itself rather than mere reachability — which is
    /// the whole point: a bad key used to read as "Reachable — HTTP 401 — Healthy".
    /// </summary>
    public sealed class AeroApiProbe(IHttpClientFactory httpClientFactory) : HttpDataSourceProbe(httpClientFactory)
    {
        public override string Key => DataSourceKeys.AeroApi;
        protected override string HttpClientName => AeroApiVerificationSource.HttpClientName;
        protected override HttpRequestMessage BuildRequest() => new(HttpMethod.Get, "account/usage");
    }

    /// <summary>GitHub does not meter the rate-limit endpoint, so this checks the token for free.</summary>
    public sealed class GitHubProbe(IHttpClientFactory httpClientFactory) : HttpDataSourceProbe(httpClientFactory)
    {
        public override string Key => DataSourceKeys.GitHub;
        protected override string HttpClientName => GitHubCiRunProvider.ApiClientName;
        protected override HttpRequestMessage BuildRequest() => new(HttpMethod.Get, "rate_limit");
    }

    /// <summary>HEAD on the dataset itself: the airports CSV is tens of megabytes, and nothing here
    /// needs its body — only that it is still being served.</summary>
    public sealed class OurAirportsProbe(IHttpClientFactory httpClientFactory) : HttpDataSourceProbe(httpClientFactory)
    {
        public override string Key => DataSourceKeys.OurAirports;
        protected override string HttpClientName => OurAirportsVerificationSource.HttpClientName;
        protected override HttpRequestMessage BuildRequest() =>
            new(HttpMethod.Head, OurAirportsVerificationSource.DatasetUrl);
    }

    /// <inheritdoc cref="OurAirportsProbe"/>
    public sealed class MledozeProbe(IHttpClientFactory httpClientFactory) : HttpDataSourceProbe(httpClientFactory)
    {
        public override string Key => DataSourceKeys.Mledoze;
        protected override string HttpClientName => MledozeCountriesVerificationSource.HttpClientName;
        protected override HttpRequestMessage BuildRequest() =>
            new(HttpMethod.Head, MledozeCountriesVerificationSource.DatasetUrl);
    }

    /// <summary>
    /// Reachability only, and the catalog's ProbeCostNote says so to the operator: every real
    /// AeroDataBox call is billed, so pressing this must never be a way to spend money.
    /// </summary>
    public sealed class AeroDataBoxProbe(IHttpClientFactory httpClientFactory) : HttpDataSourceProbe(httpClientFactory)
    {
        public override string Key => DataSourceKeys.AeroDataBox;
        protected override string HttpClientName => AeroDataBoxFlightSearchProvider.HttpClientName;
        protected override HttpRequestMessage BuildRequest() => new(HttpMethod.Head, string.Empty);
    }
}
