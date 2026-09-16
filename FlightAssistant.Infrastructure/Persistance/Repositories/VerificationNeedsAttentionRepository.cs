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
        /// </summary>
        private static Expression<Func<TEntity, bool>> NeedsAttention<TEntity>() where TEntity : SeedableEntity =>
            e => !e.IsVerified && e.VerificationIssue != null && e.VerificationIssue.Trim() != "";
    }
}
