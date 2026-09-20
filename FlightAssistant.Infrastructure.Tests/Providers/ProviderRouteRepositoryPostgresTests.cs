using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Providers;
using FlightAssistant.Domain.Providers.Entities;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Providers;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightAssistant.Infrastructure.Tests.Providers
{
    // PostgreSQL-backed because the InMemory provider would not exercise the real key constraint
    // that makes the second call an update rather than a duplicate-row insert.
    [Collection(PostgresCollection.Name)]
    public class ProviderRouteRepositoryPostgresTests : PostgresTestBase
    {
        public ProviderRouteRepositoryPostgresTests(PostgresContainerFixture fixture) : base(fixture) { }

        [Fact]
        public async Task UpsertAsync_NoExistingRow_Inserts()
        {
            await using var db = CreateDbContext();
            var repository = new ProviderRouteRepository(db);

            await repository.UpsertAsync(new ProviderRoute
            {
                Job = "flight-search-route",
                Providers = "aeroapi",
                ChangedUtc = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc),
                ChangedBy = "admin-1",
                Reason = "AeroDataBox rows looked wrong",
            }, default);

            var stored = await db.ProviderRoutes.AsNoTracking()
                .SingleAsync(r => r.Job == "flight-search-route");
            Assert.Equal("aeroapi", stored.Providers);
            Assert.Equal("admin-1", stored.ChangedBy);
        }

        [Fact]
        public async Task UpsertAsync_ExistingRow_OverwritesItRatherThanAddingASecond()
        {
            await using var db = CreateDbContext();
            var repository = new ProviderRouteRepository(db);

            // The second upsert sets every column the update branch copies, this timestamp included:
            // both upserts previously left ChangedUtc null, so dropping `existing.ChangedUtc =
            // row.ChangedUtc` from ProviderRouteRepository left this test green while the panel's
            // "changed at" went permanently stale (or stayed null forever).
            var changedUtc = new DateTime(2026, 9, 20, 8, 30, 0, DateTimeKind.Utc);

            await repository.UpsertAsync(new ProviderRoute
            {
                Job = "flight-search-route", Providers = "aeroapi", ChangedBy = "admin-1",
            }, default);
            await repository.UpsertAsync(new ProviderRoute
            {
                Job = "flight-search-route", Providers = "aerodatabox", ChangedBy = "admin-2",
                ChangedUtc = changedUtc,
                Reason = "quota restored",
            }, default);

            var rows = await db.ProviderRoutes.AsNoTracking()
                .Where(r => r.Job == "flight-search-route").ToListAsync();
            Assert.Single(rows);
            Assert.Equal("aerodatabox", rows[0].Providers);
            Assert.Equal("admin-2", rows[0].ChangedBy);
            Assert.Equal("quota restored", rows[0].Reason);
            Assert.Equal(changedUtc, rows[0].ChangedUtc);
        }

        /// <summary>
        /// The only test anywhere that proves a written row is actually read back. Every unit test
        /// substitutes <c>CachedProviderRouting</c>'s internal reader seam away, so its real
        /// <c>ReadFromDatabase</c> delegate — the scope creation, the <c>r.Job == job</c> predicate,
        /// the <c>Split</c>, and the <c>IsDefault: false</c> mapping — was exercised by nothing. A
        /// wrong predicate or a <c>Split</c> on the wrong character makes the whole feature a silent
        /// no-op that <em>fails open to pre-change behaviour</em>: every read falls back to the
        /// configured default, which is indistinguishable from "the operator pressed the button and
        /// nothing happened".
        ///
        /// <para>
        /// Built through the <b>public</b> constructor on purpose — the internal seam is what this is
        /// here to avoid — with a scope factory over a real context. The configured default is a
        /// value neither upsert writes, so a read that silently missed the row would surface as that
        /// sentinel plus <c>IsDefault: true</c> rather than as a passing assertion. Two providers, so
        /// <c>Split</c> on the wrong character cannot pass either.
        /// </para>
        /// </summary>
        [Fact]
        public async Task CachedProviderRouting_ReadsBackARowProviderRouteRepositoryWrote()
        {
            await using var seed = CreateDbContext();
            await new ProviderRouteRepository(seed).UpsertAsync(new ProviderRoute
            {
                Job = ProviderJobs.VerificationAirport,
                Providers = "ourairports,aeroapi",
                ChangedUtc = new DateTime(2026, 9, 20, 8, 30, 0, DateTimeKind.Utc),
                ChangedBy = "admin-1",
                Reason = "ask the free source first",
            }, default);

            var services = new ServiceCollection();
            services.AddScoped(_ => CreateDbContext());
            var provider = services.BuildServiceProvider();

            var routing = new CachedProviderRouting(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new SentinelDefaults(),
                TimeProvider.System,
                NullLogger<CachedProviderRouting>.Instance);

            var state = await routing.GetAsync(ProviderJobs.VerificationAirport, default);

            Assert.Equal(new[] { "ourairports", "aeroapi" }, state.Providers);
            Assert.False(state.IsDefault);
            Assert.Equal("admin-1", state.ChangedBy);

            // The cached read path resolves the same row, so Split and the predicate are proven for
            // GetProvidersAsync too -- the method every search and every provision actually calls.
            Assert.Equal(
                new[] { "ourairports", "aeroapi" },
                await routing.GetProvidersAsync(ProviderJobs.VerificationAirport, default));
        }

        /// <summary>A configured default no upsert in this class ever writes, so a read that fell back
        /// to it cannot be mistaken for a read that found the row.</summary>
        private sealed class SentinelDefaults : IProviderRouteDefaults
        {
            public IReadOnlyList<string> For(string job) => ["configured-default"];
            public bool IsCeilinged(string job) => false;
        }
    }
}
