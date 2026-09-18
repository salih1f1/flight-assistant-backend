using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Infrastructure.DataSources;
using Microsoft.Extensions.Configuration;

namespace FlightAssistant.API.Tests.HostedServices;

/// <summary>
/// #460 Task 8. The seeder answers one question at startup: which optional sources are switched
/// off in this environment? Everything else stays Unknown until something actually calls it.
/// </summary>
public class DataSourceSeederTests
{
    private sealed class RecordingObserver : IDataSourceObserver
    {
        private readonly Dictionary<string, (DataSourceOutcome Outcome, string? Detail)> _byKey = new(StringComparer.Ordinal);

        public void Record(string key, DataSourceOutcome outcome, DateTimeOffset at, string? detail = null) =>
            _byKey[key] = (outcome, detail);

        public (DataSourceOutcome Outcome, string? Detail)? For(string key) =>
            _byKey.TryGetValue(key, out var value) ? value : null;
    }

    private static IConfiguration Config(params KeyValuePair<string, string?>[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static DataSourceSeeder Seeder(RecordingObserver observer, IConfiguration config) =>
        new(new DataSourceCatalog(), observer, config, TimeProvider.System);

    [Fact]
    public async Task SeedsNotConfigured_ForAnOptionalSourceWithNoCredentials()
    {
        var observer = new RecordingObserver();

        await Seeder(observer, Config()).StartAsync(CancellationToken.None);

        Assert.Equal(DataSourceOutcome.NotConfigured, observer.For(DataSourceKeys.GitHub)!.Value.Outcome);
        Assert.Equal(DataSourceOutcome.NotConfigured, observer.For(DataSourceKeys.AeroDataBox)!.Value.Outcome);
        Assert.Equal(DataSourceOutcome.NotConfigured, observer.For(DataSourceKeys.Redis)!.Value.Outcome);
        Assert.Equal(DataSourceOutcome.NotConfigured, observer.For(DataSourceKeys.AppInsights)!.Value.Outcome);
    }

    // Without this, a required source would read NotConfigured rather than Unknown before any traffic —
    // a much weaker claim than "we have not looked yet".
    [Fact]
    public async Task DoesNotSeed_ARequiredSource()
    {
        var observer = new RecordingObserver();

        await Seeder(observer, Config()).StartAsync(CancellationToken.None);

        Assert.Null(observer.For(DataSourceKeys.AeroApi));
        Assert.Null(observer.For(DataSourceKeys.Postgres));
    }

    /// <summary>
    /// The criticality rule on its own. Every Required source in the real catalog happens to have
    /// no credential lookup, so the test above would pass even with the rule deleted — it is the
    /// missing lookup doing the work there, not the policy. This catalog gives a Required source a
    /// key the seeder *does* know how to look up, leaving only the rule to keep it unseeded.
    /// </summary>
    [Fact]
    public async Task DoesNotSeed_ARequiredSource_EvenWhenItsCredentialIsMissing()
    {
        var observer = new RecordingObserver();
        var catalog = new SingleSourceCatalog(new DataSourceDescriptor(
            DataSourceKeys.GitHub, "GitHub API", DataSourceKind.Infrastructure,
            DataSourceCriticality.Required, "whatever", ExternalUrl: null,
            HttpClientName: null, SupportsProbe: false, ProbeCostNote: null));

        await new DataSourceSeeder(catalog, observer, Config(), TimeProvider.System)
            .StartAsync(CancellationToken.None);

        Assert.Null(observer.For(DataSourceKeys.GitHub));
    }

    private sealed class SingleSourceCatalog : IDataSourceCatalog
    {
        public SingleSourceCatalog(DataSourceDescriptor source) => Sources = [source];

        public IReadOnlyList<DataSourceDescriptor> Sources { get; }

        public DataSourceDescriptor? TryGet(string key) =>
            Sources.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoesNotSeed_AnOptionalSourceThatIsConfigured()
    {
        var observer = new RecordingObserver();

        await Seeder(observer, Config(new KeyValuePair<string, string?>("GitHub:Token", "ghp_example"))).StartAsync(CancellationToken.None);

        Assert.Null(observer.For(DataSourceKeys.GitHub));
    }

    /// <summary>
    /// App Service sets its own variable rather than the documented key (#440), and the seeder must
    /// read it the same way Program.cs does — otherwise a live App Insights would read "not
    /// configured" on every App Service deployment.
    /// </summary>
    [Fact]
    public async Task DoesNotSeedAppInsights_WhenOnlyThePlatformVariableIsSet()
    {
        var observer = new RecordingObserver();

        await Seeder(observer, Config(new KeyValuePair<string, string?>("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=abc")))
            .StartAsync(CancellationToken.None);

        Assert.Null(observer.For(DataSourceKeys.AppInsights));
    }
}
