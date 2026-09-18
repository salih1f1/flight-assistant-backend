using FlightAssistant.Application.DataSources;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.DataSources;
using FlightAssistant.Domain.DataSources.Entities;
using Xunit;

namespace FlightAssistant.Application.Tests.Queries.Admin.DataSources
{
    /// <summary>
    /// #460 Task 9. The catalog arrives through <see cref="IDataSourceCatalog"/> rather than as the
    /// real <c>DataSourceCatalog</c>: that type lives in Infrastructure, which this project must not
    /// reference (CLAUDE.md layering). <c>AdminDataSourcesEndpointTests</c> covers the real catalog.
    /// </summary>
    public class GetDataSourcesQueryTests
    {
        private static readonly DateTime Now = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

        private const string Required = "required-source";
        private const string Optional = "optional-source";

        private static DataSourceDescriptor Descriptor(string key, DataSourceCriticality criticality) =>
            new(key, $"The {key}", DataSourceKind.Infrastructure, criticality,
                $"Checks {key}.", ExternalUrl: null, HttpClientName: null,
                SupportsProbe: true, ProbeCostNote: null);

        private sealed class FakeCatalog : IDataSourceCatalog
        {
            public IReadOnlyList<DataSourceDescriptor> Sources { get; } =
            [
                Descriptor(Required, DataSourceCriticality.Required),
                Descriptor(Optional, DataSourceCriticality.Optional),
            ];

            public DataSourceDescriptor? TryGet(string key) =>
                Sources.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
        }

        private sealed class FakeRepository : IDataSourceStatusRepository
        {
            private readonly IReadOnlyList<DataSourceStatus> _rows;

            public FakeRepository(IReadOnlyList<DataSourceStatus> rows) => _rows = rows;

            public Task<IReadOnlyList<DataSourceStatus>> GetAllAsync(CancellationToken ct) => Task.FromResult(_rows);

            public Task<DataSourceStatus?> GetAsync(string key, CancellationToken ct) =>
                Task.FromResult(_rows.FirstOrDefault(r => r.Key == key));

            public Task UpsertAsync(DataSourceStatus row, CancellationToken ct) => Task.CompletedTask;
        }

        private static GetDataSourcesQueryHandler Handler(params DataSourceStatus[] storedRows) =>
            new(new FakeCatalog(), new FakeRepository(storedRows), TimeProvider.System);

        [Fact]
        public async Task ReturnsOneRowPerCatalogSource_EvenWhenNeverObserved()
        {
            var result = await Handler().Handle(new GetDataSourcesQuery(), CancellationToken.None);

            Assert.Equal(2, result.Count);
            Assert.All(result, r => Assert.Equal(nameof(DataSourceStatusValue.Unknown), r.Status));
        }

        [Fact]
        public async Task AnObservedSource_CarriesItsDerivedStatus()
        {
            var result = await Handler(new DataSourceStatus { Key = Required, LastOutcome = "Unauthorized", LastOutcomeUtc = Now })
                .Handle(new GetDataSourcesQuery(), CancellationToken.None);

            Assert.Equal(nameof(DataSourceStatusValue.Unhealthy), result.Single(r => r.Key == Required).Status);
        }

        /// <summary>The same stored outcome reads differently by criticality — see the evaluator.</summary>
        [Fact]
        public async Task AnOptionalSource_IsOnlyDegraded_ByTheSameOutcome()
        {
            var result = await Handler(new DataSourceStatus { Key = Optional, LastOutcome = "Unauthorized", LastOutcomeUtc = Now })
                .Handle(new GetDataSourcesQuery(), CancellationToken.None);

            Assert.Equal(nameof(DataSourceStatusValue.Degraded), result.Single(r => r.Key == Optional).Status);
        }

        [Fact]
        public async Task AStoredRowWithNoCatalogEntry_IsIgnored()
        {
            var result = await Handler(new DataSourceStatus { Key = "removed-source", LastOutcome = "Ok" })
                .Handle(new GetDataSourcesQuery(), CancellationToken.None);

            Assert.DoesNotContain(result, r => r.Key == "removed-source");
        }

        [Fact]
        public async Task AnUnreadableStoredOutcome_ReadsAsNeverObserved()
        {
            var result = await Handler(new DataSourceStatus { Key = Required, LastOutcome = "NotAnOutcome", LastOutcomeUtc = Now })
                .Handle(new GetDataSourcesQuery(), CancellationToken.None);

            Assert.Equal(nameof(DataSourceStatusValue.Unknown), result.Single(r => r.Key == Required).Status);
        }

        [Fact]
        public async Task LastError_IsOmitted_WhenDiagnosticsAreNotIncluded()
        {
            var result = await Handler(new DataSourceStatus { Key = Required, LastOutcome = "Failed", LastError = "aeroapi returned HTTP 503" })
                .Handle(new GetDataSourcesQuery(IncludeDiagnostics: false), CancellationToken.None);

            Assert.Null(result.Single(r => r.Key == Required).LastError);
        }

        [Fact]
        public async Task LastError_IsIncluded_WhenDiagnosticsAre()
        {
            var result = await Handler(new DataSourceStatus { Key = Required, LastOutcome = "Failed", LastError = "aeroapi returned HTTP 503" })
                .Handle(new GetDataSourcesQuery(IncludeDiagnostics: true), CancellationToken.None);

            Assert.Equal("aeroapi returned HTTP 503", result.Single(r => r.Key == Required).LastError);
        }

        [Fact]
        public async Task SourcesComeBackInCatalogOrder()
        {
            var result = await Handler().Handle(new GetDataSourcesQuery(), CancellationToken.None);

            Assert.Equal(new[] { Required, Optional }, result.Select(r => r.Key));
        }
    }
}
