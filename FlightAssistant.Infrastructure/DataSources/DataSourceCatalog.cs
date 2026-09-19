using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using FlightAssistant.Infrastructure.ExternalServices.Azure;
using FlightAssistant.Infrastructure.ExternalServices.GitHub;
using FlightAssistant.Infrastructure.ExternalServices.Verification;

namespace FlightAssistant.Infrastructure.DataSources
{
    /// <summary>Catalog keys. Public because they are the status table's primary key and appear in URLs.</summary>
    public static class DataSourceKeys
    {
        public const string Postgres = "postgres";
        public const string AeroApi = "aeroapi";
        public const string AeroDataBox = "aerodatabox";
        public const string OurAirports = "ourairports";
        public const string Mledoze = "mledoze";
        public const string GitHub = "github";
        public const string Redis = "redis";
        public const string Apns = "apns";
        public const string AppInsights = "appinsights";

        // #496 task 3: AzureCostProvider's named HttpClient was registered under this key in task
        // 2, for AddDataSourceObservation to tag it with. The DataSourceDescriptor below is what
        // makes EveryNamedHttpClientHasACatalogEntry pass again (task 2 deliberately left it red).
        public const string AzureCost = "azurecost";
    }

    /// <summary>
    /// The ten outside dependencies, as the health page describes them. A hard-coded list rather
    /// than a scan: the "what is checked" sentence and the link are author-written prose that
    /// cannot be derived, and the completeness test is what keeps the list honest.
    /// </summary>
    public sealed class DataSourceCatalog : IDataSourceCatalog
    {
        private readonly IReadOnlyDictionary<string, DataSourceDescriptor> _byKey;

        public DataSourceCatalog()
        {
            Sources =
            [
                new(DataSourceKeys.Postgres, "PostgreSQL", DataSourceKind.Database,
                    DataSourceCriticality.Required,
                    "Opens a connection to the application database.",
                    ExternalUrl: null, HttpClientName: null, SupportsProbe: true, ProbeCostNote: null),

                new(DataSourceKeys.AeroApi, "FlightAware AeroAPI", DataSourceKind.FlightData,
                    DataSourceCriticality.Required,
                    "Signs in with the AeroAPI key and reads the account-usage report, which FlightAware bills at $0.00.",
                    "https://www.flightaware.com/aeroapi/portal/",
                    AeroApiVerificationSource.HttpClientName, SupportsProbe: true, ProbeCostNote: null),

                new(DataSourceKeys.AeroDataBox, "AeroDataBox (RapidAPI)", DataSourceKind.FlightData,
                    DataSourceCriticality.Optional,
                    "Checks that the AeroDataBox host answers. It does not verify the API key: every real AeroDataBox call is billed.",
                    "https://rapidapi.com/developer/dashboard",
                    AeroDataBoxFlightSearchProvider.HttpClientName, SupportsProbe: true,
                    ProbeCostNote: "Reachability only — every real AeroDataBox call is billed, so the key itself is not tested."),

                new(DataSourceKeys.OurAirports, "OurAirports", DataSourceKind.ReferenceData,
                    DataSourceCriticality.Required,
                    "Checks that the airports dataset is being served.",
                    "https://ourairports.com/data/",
                    OurAirportsVerificationSource.HttpClientName, SupportsProbe: true, ProbeCostNote: null),

                new(DataSourceKeys.Mledoze, "mledoze/countries", DataSourceKind.ReferenceData,
                    DataSourceCriticality.Required,
                    "Checks that the countries dataset is being served.",
                    "https://github.com/mledoze/countries",
                    MledozeCountriesVerificationSource.HttpClientName, SupportsProbe: true, ProbeCostNote: null),

                new(DataSourceKeys.GitHub, "GitHub API", DataSourceKind.Infrastructure,
                    DataSourceCriticality.Optional,
                    "Reads the rate-limit endpoint with the configured token, which GitHub does not meter.",
                    "https://github.com/salih1f1/flight-assistant-backend/actions",
                    GitHubCiRunProvider.ApiClientName, SupportsProbe: true, ProbeCostNote: null),

                // The download client shares the GitHub row: it is the same host and the same
                // token. Listed so the completeness test sees it covered.
                new($"{DataSourceKeys.GitHub}-download", "GitHub artifact download", DataSourceKind.Infrastructure,
                    DataSourceCriticality.Optional,
                    "Downloads CI test-result archives. Covered by the GitHub API check above.",
                    ExternalUrl: null, GitHubCiRunProvider.DownloadClientName,
                    SupportsProbe: false, ProbeCostNote: null),

                new(DataSourceKeys.Redis, "Redis cache", DataSourceKind.Infrastructure,
                    DataSourceCriticality.Optional,
                    "Writes a short-lived probe key and reads it back.",
                    ExternalUrl: null, HttpClientName: null, SupportsProbe: true, ProbeCostNote: null),

                new(DataSourceKeys.Apns, "Apple Push Notification service", DataSourceKind.Notifications,
                    DataSourceCriticality.Required,
                    "Signs a provider token with the configured P8 key. No push is sent.",
                    "https://developer.apple.com/account/resources/authkeys/list",
                    HttpClientName: null, SupportsProbe: true, ProbeCostNote: null),

                new(DataSourceKeys.AppInsights, "Application Insights", DataSourceKind.Telemetry,
                    DataSourceCriticality.Optional,
                    "Checks only that a connection string resolves. Telemetry leaves through the SDK's own pipeline, which this process cannot observe.",
                    ExternalUrl: null, HttpClientName: null, SupportsProbe: true, ProbeCostNote: null),

                // #496 task 3. Optional because the application runs fine without it -- a local
                // developer has no subscription configured, same as GitHub above. Cost Management
                // reads are not billed, so ProbeCostNote is null (contrast AeroDataBox, where every
                // real call costs money and the probe must say so before it fires).
                new(DataSourceKeys.AzureCost, "Azure Cost Management", DataSourceKind.Infrastructure,
                    DataSourceCriticality.Optional,
                    "Reads the subscription's budget and month-to-date spend. Reports only; it cannot change anything in Azure.",
                    "https://portal.azure.com/#view/Microsoft_Azure_CostManagement/Menu/~/costanalysis",
                    AzureCostProvider.HttpClientName, SupportsProbe: true, ProbeCostNote: null),
            ];

            _byKey = Sources.ToDictionary(s => s.Key, StringComparer.Ordinal);
        }

        public IReadOnlyList<DataSourceDescriptor> Sources { get; }

        public DataSourceDescriptor? TryGet(string key) =>
            !string.IsNullOrWhiteSpace(key) && _byKey.TryGetValue(key, out var source) ? source : null;
    }
}
