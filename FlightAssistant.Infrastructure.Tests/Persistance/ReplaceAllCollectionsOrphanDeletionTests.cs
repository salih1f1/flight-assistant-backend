using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// Verifies that <see cref="Lounge.Providers"/> / <see cref="LoyaltyProgram.Tiers"/> are correctly
/// mapped by EF Core as <see cref="IReadOnlyCollection{T}"/> navigations backed by the
/// <c>_providers</c>/<c>_tiers</c> private fields (per the "field" naming convention), and that
/// <see cref="Lounge.ReplaceAllProviders"/> / <see cref="LoyaltyProgram.ReplaceAllTiers"/> result
/// in orphaned child rows being deleted on save (EF Core's "delete orphans" / fixup behavior for
/// a required, fully-loaded collection navigation).
/// </summary>
public class ReplaceAllCollectionsOrphanDeletionTests
{
    private static FlightAssistantDbContext CreateContext(string dbName) =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Fact]
    public async Task ReplaceAllProviders_RemovesOrphanedLoungeProviderLinks()
    {
        var dbName = Guid.NewGuid().ToString();

        int loungeId;
        int providerAId;
        int providerBId;

        // Arrange: create a lounge with two provider links.
        using (var ctx = CreateContext(dbName))
        {
            var airport = Airport.Create("JFK", "KJFK", "John F. Kennedy International Airport", "New York", "US");
            ctx.Airports.Add(airport);
            await ctx.SaveChangesAsync();

            var providerA = LoungeProvider.Create("PRIORITY_PASS", "Priority Pass", LoungeProviderKind.Contract);
            var providerB = LoungeProvider.Create("AMEX_CENTURION", "Centurion Lounge", LoungeProviderKind.Card);
            ctx.LoungeProviders.AddRange(providerA, providerB);
            await ctx.SaveChangesAsync();

            providerAId = providerA.Id;
            providerBId = providerB.Id;

            var lounge = Lounge.Create(airport.Id, "Test Lounge", "T1");
            lounge.ReplaceAllProviders(new[]
            {
                LoungeProviderLink.Create(0, providerAId, LoungeProviderRole.AccessNetwork),
                LoungeProviderLink.Create(0, providerBId, LoungeProviderRole.AccessNetwork)
            });

            ctx.Lounges.Add(lounge);
            await ctx.SaveChangesAsync();

            loungeId = lounge.Id;
        }

        // Verify both links were persisted.
        using (var ctx = CreateContext(dbName))
        {
            var linkCount = await ctx.LoungeProviderLinks.CountAsync(l => l.LoungeId == loungeId);
            Assert.Equal(2, linkCount);
        }

        // Act: reload the lounge, replace its providers with only one of the original two.
        using (var ctx = CreateContext(dbName))
        {
            var existing = await ctx.Lounges
                .Include(l => l.Providers)
                .SingleAsync(l => l.Id == loungeId);

            var keep = existing.Providers.Single(p => p.ProviderId == providerAId);
            existing.ReplaceAllProviders(new[]
            {
                LoungeProviderLink.Create(loungeId, keep.ProviderId, keep.Role, keep.Notes, keep.MinAllianceTierCode)
            });

            await ctx.SaveChangesAsync();
        }

        // Assert: the removed provider link was deleted, not left as an orphan.
        using (var ctx = CreateContext(dbName))
        {
            var remaining = await ctx.LoungeProviderLinks.Where(l => l.LoungeId == loungeId).ToListAsync();
            Assert.Single(remaining);
            Assert.Equal(providerAId, remaining[0].ProviderId);
        }
    }

    [Fact]
    public async Task ReplaceAllTiers_RemovesOrphanedLoyaltyTiers()
    {
        var dbName = Guid.NewGuid().ToString();

        int programId;

        // Arrange: create a loyalty program with two tiers.
        using (var ctx = CreateContext(dbName))
        {
            var kind = LoyaltyProgramKind.Create("FREQUENT_FLYER", "Frequent Flyer");
            ctx.LoyaltyProgramKinds.Add(kind);
            await ctx.SaveChangesAsync();

            var program = LoyaltyProgram.Create("TEST_FFP", "Test Frequent Flyer Program", kind.Id);
            program.AddTier(LoyaltyTier.Create(0, "SILVER", "Silver", 1));
            program.AddTier(LoyaltyTier.Create(0, "GOLD", "Gold", 2));

            ctx.LoyaltyPrograms.Add(program);
            await ctx.SaveChangesAsync();

            programId = program.Id;
        }

        // Verify both tiers were persisted.
        using (var ctx = CreateContext(dbName))
        {
            var tierCount = await ctx.LoyaltyTiers.CountAsync(t => t.ProgramId == programId);
            Assert.Equal(2, tierCount);
        }

        // Act: reload the program, replace its tiers with only one (silver dropped, gold kept, recreated).
        using (var ctx = CreateContext(dbName))
        {
            var existing = await ctx.LoyaltyPrograms
                .Include(p => p.Tiers)
                .SingleAsync(p => p.Id == programId);

            existing.ReplaceAllTiers(new[]
            {
                LoyaltyTier.Create(existing.Id, "GOLD", "Gold", 2)
            });

            await ctx.SaveChangesAsync();
        }

        // Assert: the removed tier was deleted, not left as an orphan.
        using (var ctx = CreateContext(dbName))
        {
            var remaining = await ctx.LoyaltyTiers.Where(t => t.ProgramId == programId).ToListAsync();
            Assert.Single(remaining);
            Assert.Equal("GOLD", remaining[0].Code);
        }
    }
}
