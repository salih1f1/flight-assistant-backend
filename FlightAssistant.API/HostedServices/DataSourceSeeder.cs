using FlightAssistant.API.Infrastructure;
using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Infrastructure.DataSources;

namespace FlightAssistant.API.HostedServices
{
    /// <summary>
    /// Records <see cref="DataSourceOutcome.NotConfigured"/> at startup for each optional source
    /// this environment has no credentials for, so the health page can say "switched off here"
    /// instead of leaving a row that will never be observed sitting at Unknown for ever.
    ///
    /// <para>
    /// Required sources are deliberately not seeded. They are called in the normal course of the
    /// application's work, so their first real observation arrives on its own — and until it does,
    /// Unknown ("we have not looked yet") is the honest answer. Seeding them would claim something
    /// stronger than the process knows.
    /// </para>
    ///
    /// <para>
    /// It reads configuration only: no connection is opened and no request is sent, so a source
    /// that <em>is</em> configured stays Unknown here too, until something calls it.
    /// </para>
    /// </summary>
    public sealed class DataSourceSeeder : IHostedService
    {
        private readonly IDataSourceCatalog _catalog;
        private readonly IDataSourceObserver _observer;
        private readonly IConfiguration _configuration;
        private readonly TimeProvider _timeProvider;

        public DataSourceSeeder(
            IDataSourceCatalog catalog,
            IDataSourceObserver observer,
            IConfiguration configuration,
            TimeProvider timeProvider)
        {
            _catalog = catalog;
            _observer = observer;
            _configuration = configuration;
            _timeProvider = timeProvider;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var now = _timeProvider.GetUtcNow();

            foreach (var source in _catalog.Sources)
            {
                if (source.Criticality != DataSourceCriticality.Optional) continue;
                if (IsConfigured(source.Key) is not false) continue;

                _observer.Record(source.Key, DataSourceOutcome.NotConfigured, now, NotConfiguredDetail(source));
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// True/false for an optional source whose credential this knows how to look up, and null
        /// for one it does not — null meaning "no opinion", which leaves the row at Unknown rather
        /// than declaring a source unconfigured on no evidence.
        /// </summary>
        private bool? IsConfigured(string key) => key switch
        {
            DataSourceKeys.GitHub => Has(_configuration["GitHub:Token"]),
            // The artifact-download client shares the GitHub token and the GitHub row's fate.
            $"{DataSourceKeys.GitHub}-download" => Has(_configuration["GitHub:Token"]),
            DataSourceKeys.AeroDataBox => Has(_configuration["AeroDataBox:ApiKey"]),
            DataSourceKeys.Redis => Has(_configuration.GetConnectionString("Redis")),
            // Resolve, not the documented key alone: App Service sets its own variable (#440).
            DataSourceKeys.AppInsights => Has(ApplicationInsightsConnection.Resolve(_configuration)),
            _ => null,
        };

        private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

        private static string NotConfiguredDetail(DataSourceDescriptor source) =>
            $"{source.DisplayName} is not configured in this environment";
    }
}
