using System.Linq.Expressions;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    /// <summary>
    /// <see cref="IVerificationNeedsAttentionRepository"/> against <c>Airport</c>, <c>Airline</c> and
    /// <c>Country</c> (#385, Task 2): one <c>COUNT(*)</c> per table, always all three, in enum order.
    /// </summary>
    public class VerificationNeedsAttentionRepository : IVerificationNeedsAttentionRepository
    {
        private readonly FlightAssistantDbContext _db;
        public VerificationNeedsAttentionRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<IReadOnlyList<VerificationNeedsAttentionCount>> GetCountsAsync(CancellationToken ct = default) =>
        [
            new(VerificationEntityType.Airport, await _db.Airports.CountAsync(NeedsAttention<Airport>(), ct)),
            new(VerificationEntityType.Airline, await _db.Airlines.CountAsync(NeedsAttention<Airline>(), ct)),
            new(VerificationEntityType.Country, await _db.Countries.CountAsync(NeedsAttention<Country>(), ct)),
        ];

        /// <summary>
        /// <c>IsVerified</c> false and <c>VerificationIssue</c> non-null and non-blank (spec §2.1).
        /// <see cref="string.IsNullOrWhiteSpace(string?)"/> does not translate in EF Core -- it throws,
        /// or silently evaluates client-side and scans the whole table -- so this is a null check plus
        /// <see cref="string.Trim()"/>, which Npgsql translates to <c>btrim(...) &lt;&gt; ''</c>. Generic
        /// over <typeparamref name="TEntity"/> because <c>Airport</c>, <c>Airline</c> and <c>Country</c>
        /// share no common <c>DbSet</c>, only the <see cref="SeedableEntity"/> base the two verification
        /// columns live on.
        /// <para>
        /// The <c>!e.IsVerified</c> conjunct here is redundant with the admin data browser's
        /// <c>VerificationIssue:*</c> filter (spec §4.7) only because of an invariant this predicate
        /// does not itself enforce: every <see cref="SeedableEntity"/> mutator that leaves
        /// <c>VerificationIssue</c> non-null also leaves <c>IsVerified</c> false. The dashboard card
        /// built from this count links to that filter (a general-purpose column filter with no
        /// knowledge of <c>IsVerified</c>), so the two must keep selecting the same rows. The
        /// invariant itself is pinned by
        /// <c>SeedableEntityVerificationTests.ANonNullVerificationIssue_AlwaysImpliesNotVerified</c>
        /// (Domain.Tests), not here -- if it ever broke, this predicate and the filter would silently
        /// disagree on which rows "need attention".
        /// </para>
        /// </summary>
        private static Expression<Func<TEntity, bool>> NeedsAttention<TEntity>() where TEntity : SeedableEntity =>
            e => !e.IsVerified && e.VerificationIssue != null && e.VerificationIssue.Trim() != "";
    }
}
