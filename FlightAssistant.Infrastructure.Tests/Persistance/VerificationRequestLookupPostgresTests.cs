using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using FlightAssistant.Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// <see cref="VerificationRequestRepository.GetRecentForEntityAsync"/> (#385, Task 1): the admin
/// panel's "this entity's recent checks" lookup. PostgreSQL-backed because what is under test is real
/// SQL behaviour InMemory cannot show: the ordering (<c>RequestedUtc</c> descending, then <c>Id</c>
/// descending as the tiebreaker for two requests sharing a timestamp -- PostgreSQL gives no order for
/// free) and the <c>LIMIT</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class VerificationRequestLookupPostgresTests : PostgresTestBase
{
    private static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    public VerificationRequestLookupPostgresTests(PostgresContainerFixture fixture) : base(fixture) { }

    /// <summary>
    /// Five requests for the target entity (Airport "42"), plus one for another entity of the same
    /// type (Airport "43") and one for the same key but a different type (Airline "42") -- neither of
    /// which may leak into the result. Asking for a limit of 3 out of the target's 5 requests proves
    /// the ordering (newest <c>RequestedUtc</c> first), the entity-type-and-key filter, and the
    /// <c>LIMIT</c> in one query.
    /// </summary>
    [Fact]
    public async Task GetRecentForEntityAsync_ReturnsTheNewestLimitForThatEntityAndType_AndNothingElse()
    {
        var ids = await SeedEachAsync(
            Request("42", T0),
            Request("42", T0.AddMinutes(1)),
            Request("42", T0.AddMinutes(2)),
            Request("42", T0.AddMinutes(3)),
            Request("42", T0.AddMinutes(4)),
            Request("43", T0.AddMinutes(5)),                                             // another entity, same type
            Request("42", T0.AddMinutes(6), entityType: VerificationEntityType.Airline)); // same key, another type

        await using var db = CreateDbContext();
        var recent = await Repository(db).GetRecentForEntityAsync(VerificationEntityType.Airport, "42", limit: 3);

        // Newest three of the target's five requests, newest first. Each request was saved in its
        // own SaveChanges (see SeedEachAsync), so ids[4] > ids[3] > ids[2] > ids[1] > ids[0].
        Assert.Equal(new[] { ids[4], ids[3], ids[2] }, recent.Select(r => r.Id));
        Assert.All(recent, r =>
        {
            Assert.Equal(VerificationEntityType.Airport, r.EntityType);
            Assert.Equal("42", r.EntityKey);
        });
    }

    /// <summary>
    /// Two requests for the same entity share <c>T0</c> exactly. The row inserted <em>first</em>
    /// (the lower id) is deliberately the one that must come back <em>second</em>: a bare
    /// "ORDER BY requested_utc DESC" with no id tiebreaker has been <em>observed</em> to return ties
    /// from a freshly-inserted, never-updated table in physical (insertion) order on real PostgreSQL --
    /// the opposite of what "descending id" requires here -- but PostgreSQL makes no such ordering
    /// guarantee for ties, so this is not one either. Dropping the tiebreaker is therefore expected to
    /// fail this test by construction (insertion order opposes id order) rather than by luck of an
    /// unordered tie, but if a future PostgreSQL version, plan, or storage layout ever makes it pass
    /// without the tiebreaker, re-run the break (drop the <c>ThenByDescending(r => r.Id)</c> in
    /// <see cref="VerificationRequestRepository.GetRecentForEntityAsync"/> and confirm this test still
    /// fails) before trusting it again.
    /// </summary>
    [Fact]
    public async Task GetRecentForEntityAsync_TwoRequestsSharingARequestedUtc_ComeBackByDescendingId()
    {
        var ids = await SeedEachAsync(Request("77", T0), Request("77", T0));

        await using var db = CreateDbContext();
        var recent = await Repository(db).GetRecentForEntityAsync(VerificationEntityType.Airport, "77", limit: 10);

        Assert.Equal(new[] { ids[1], ids[0] }, recent.Select(r => r.Id));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static VerificationRequestRepository Repository(FlightAssistantDbContext db) =>
        new(db, new FakeTimeProvider(new DateTimeOffset(T0)));

    /// <summary>
    /// Completed immediately (<see cref="VerificationRequest.MarkVerified"/>): the partial unique
    /// index allows only one <em>open</em> (Pending/Running) request per entity, and both tests here
    /// seed several requests for the same entity, so each must be resolved before the next can be
    /// inserted. The lookup being tested doesn't filter on status, so this doesn't affect it.
    /// </summary>
    private static VerificationRequest Request(
        string key, DateTime requestedUtc, VerificationEntityType entityType = VerificationEntityType.Airport)
    {
        var request = VerificationRequest.Create(
            entityType, key, new[] { "ourairports" }, VerificationTrigger.Admin, "admin-oid", 0m, requestedUtc);
        request.MarkVerified(requestedUtc);
        return request;
    }

    /// <summary>Saves each request in its own <c>SaveChanges</c>, so ids follow argument (insertion) order.</summary>
    private async Task<long[]> SeedEachAsync(params VerificationRequest[] requests)
    {
        await using var db = CreateDbContext();
        foreach (var request in requests)
        {
            db.VerificationRequests.Add(request);
            await db.SaveChangesAsync();
        }
        return requests.Select(r => r.Id).ToArray();
    }
}
