using FlightAssistant.Domain.Providers.Entities;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

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

            await repository.UpsertAsync(new ProviderRoute
            {
                Job = "flight-search-route", Providers = "aeroapi", ChangedBy = "admin-1",
            }, default);
            await repository.UpsertAsync(new ProviderRoute
            {
                Job = "flight-search-route", Providers = "aerodatabox", ChangedBy = "admin-2",
                Reason = "quota restored",
            }, default);

            var rows = await db.ProviderRoutes.AsNoTracking()
                .Where(r => r.Job == "flight-search-route").ToListAsync();
            Assert.Single(rows);
            Assert.Equal("aerodatabox", rows[0].Providers);
            Assert.Equal("admin-2", rows[0].ChangedBy);
            Assert.Equal("quota restored", rows[0].Reason);
        }
    }
}
