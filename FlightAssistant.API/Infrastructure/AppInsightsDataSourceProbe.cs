using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Infrastructure.DataSources;

namespace FlightAssistant.API.Infrastructure
{
    /// <summary>
    /// Checks only that a connection string resolves — which is all this process can honestly say
    /// about Application Insights: telemetry leaves through the SDK's own background pipeline, which
    /// nothing here observes. The catalog's <c>WhatIsChecked</c> tells the operator exactly that,
    /// rather than showing a green light that means less than it looks like.
    ///
    /// <para>
    /// It lives in the API layer, not beside the other probes in Infrastructure, because
    /// <see cref="ApplicationInsightsConnection"/> does: App Service sets its own variable rather
    /// than the documented key (#440), and duplicating that fallback in Infrastructure would give
    /// the two copies something to drift on.
    /// </para>
    /// </summary>
    public sealed class AppInsightsDataSourceProbe(
        IConfiguration configuration,
        IDataSourceObserver observer,
        TimeProvider timeProvider) : IDataSourceProbe
    {
        public string Key => DataSourceKeys.AppInsights;

        public Task<DataSourceOutcome> ProbeAsync(CancellationToken ct)
        {
            var outcome = ApplicationInsightsConnection.Resolve(configuration) is null
                ? DataSourceOutcome.NotConfigured
                : DataSourceOutcome.Ok;

            observer.Record(Key, outcome, timeProvider.GetUtcNow(),
                outcome == DataSourceOutcome.NotConfigured
                    ? "No Application Insights connection string is configured"
                    : null);

            return Task.FromResult(outcome);
        }
    }
}
