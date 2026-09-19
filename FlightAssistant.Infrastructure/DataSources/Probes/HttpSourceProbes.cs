using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Hosting;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Usage;
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

    /// <summary>
    /// Delegates to <see cref="IAzureCostProvider"/> instead of extending <see cref="HttpDataSourceProbe"/>
    /// like the probes above: reaching Cost Management needs a bearer token fetched asynchronously
    /// (Azure AD, not an API key on the client), and <see cref="HttpDataSourceProbe.BuildRequest"/>
    /// is a synchronous method called outside its own try/catch, so a credential failure there would
    /// escape <c>ProbeAsync</c> as an unhandled exception -- breaking the "never throws" contract
    /// <see cref="IDataSourceProbe"/> documents. The provider already does this correctly (#496 task
    /// 2: every expected failure, auth included, maps to an <see cref="AzureBudgetState"/> and never
    /// throws), so reusing it is the safer and, given it is also the exact production code path,
    /// the more faithful answer to "does the operator's role actually work" than a second
    /// hand-rolled request would be. It still reaches Azure through the named
    /// <c>AzureCost</c> client, so <c>DataSourceObservingHandler</c> already records the HTTP call —
    /// this must not record a second time.
    /// </summary>
    public sealed class AzureCostProbe(IAzureCostProvider provider) : IDataSourceProbe
    {
        public string Key => DataSourceKeys.AzureCost;

        public async Task<DataSourceOutcome> ProbeAsync(CancellationToken ct)
        {
            var snapshot = await provider.GetBudgetAsync(ct);

            // NoBudget still answered from Cost Management with a clean response, which already
            // proves the role is granted -- the question this probe exists to answer -- so it counts
            // as Ok here, the same way the dashboard card does not treat "no budget defined" as a
            // fault.
            // ACCEPTED DEVIATION: a 429 from Cost Management reaches here as
            // AzureBudgetState.Unavailable and is mapped to Failed below, but the same HTTP
            // response, seen by DataSourceObservingHandler on the underlying "AzureCost" client, is
            // recorded as RateLimited -- so the probe outcome and the observed outcome disagree for
            // this one source. Left as-is deliberately: adding a RateLimited branch here would need
            // a state the panel has no use for (AzureBudgetState carries no RateLimited of its own,
            // only Unavailable), and threading one through purely to match an observation the probe
            // doesn't otherwise use is the scope creep YAGNI rules out.
            return snapshot.State switch
            {
                AzureBudgetState.Ok or AzureBudgetState.NoBudget => DataSourceOutcome.Ok,
                AzureBudgetState.NotConfigured => DataSourceOutcome.NotConfigured,
                AzureBudgetState.NotAuthorized => DataSourceOutcome.Unauthorized,
                _ => DataSourceOutcome.Failed,
            };
        }
    }

    /// <summary>
    /// Delegates to <see cref="IAppServiceProvider"/> instead of extending <see cref="HttpDataSourceProbe"/>,
    /// for the same reason <see cref="AzureCostProbe"/> above does: reading the site needs a bearer
    /// token fetched asynchronously (Azure AD, not an API key on the client), and
    /// <see cref="HttpDataSourceProbe.BuildRequest"/> is a synchronous method called outside its own
    /// try/catch, so a credential failure there would escape <c>ProbeAsync</c> as an unhandled
    /// exception -- breaking the "never throws" contract <see cref="IDataSourceProbe"/> documents,
    /// and skip both the status write and the audit write on the one screen an operator uses to
    /// diagnose a credential problem. The provider already maps every expected failure to an
    /// <see cref="AppServiceState"/> and never throws (#530 task 2), so reusing it is both the safer
    /// answer and the actual production code path. It reaches Azure through the named
    /// <c>AppService</c> client, so <c>DataSourceObservingHandler</c> already records the HTTP call —
    /// this must not record a second time.
    ///
    /// <para>
    /// It inherits <see cref="AzureCostProbe"/>'s accepted deviation too, so it is written down here
    /// rather than left to be rediscovered: ARM answering 429 lands on <c>Failed</c>, while the
    /// observing handler records the same response as <c>RateLimited</c>. The provider collapses
    /// every non-401/403 status into <see cref="AppServiceState.Unavailable"/>, and a throttled read
    /// is a read that did not happen -- which is what <c>Failed</c> says.
    /// </para>
    /// </summary>
    public sealed class AppServiceProbe(IAppServiceProvider provider) : IDataSourceProbe
    {
        public string Key => DataSourceKeys.AppService;

        public async Task<DataSourceOutcome> ProbeAsync(CancellationToken ct)
        {
            var snapshot = await provider.GetStatusAsync(ct);

            // AlwaysOn is never consulted here, deliberately: this probe reports on whether the
            // resource could be READ, never on what the answer was. A site with Always On off, read
            // successfully, is State=Ok -- see AppServiceProvider's own note on State vs SiteState.
            return snapshot.State switch
            {
                AppServiceState.Ok => DataSourceOutcome.Ok,
                AppServiceState.NotConfigured => DataSourceOutcome.NotConfigured,
                AppServiceState.NotAuthorized => DataSourceOutcome.Unauthorized,
                _ => DataSourceOutcome.Failed,
            };
        }
    }
}
