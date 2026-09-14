using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class AdminStatsRepository : IAdminStatsRepository
    {
        /// <summary>
        /// <c>SeedableEntity.SourceType</c>'s default, which the seeder leaves in place. A seeded row's
        /// <c>FirstSeenAt</c> is the seed run, not a record anyone added, so it never counts as new.
        /// </summary>
        private const string SeedSourceType = "seed";

        private readonly FlightAssistantDbContext _db;
        public AdminStatsRepository(FlightAssistantDbContext db) => _db = db;

        // Every query runs one after another: a DbContext can't run two at once. Each is a COUNT or
        // a single-column projection bounded by the window, so none pulls a table into memory.
        public async Task<IReadOnlyList<AdminEntityCounts>> GetCountsAsync(
            DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        [
            new("travelers", nameof(Traveler.CreatedUtc),
                await _db.Travelers.CountAsync(ct),
                null,
                await _db.Travelers
                    .Where(t => t.CreatedUtc >= fromUtc && t.CreatedUtc < toUtc)
                    .Select(t => t.CreatedUtc)
                    .ToListAsync(ct)),

            new("flights", nameof(Flight.CreatedUtc),
                await _db.Flights.CountAsync(ct),
                null,
                await _db.Flights
                    .Where(f => f.CreatedUtc >= fromUtc && f.CreatedUtc < toUtc)
                    .Select(f => f.CreatedUtc)
                    .ToListAsync(ct)),

            new("airlines", nameof(Airline.FirstSeenAt),
                await _db.Airlines.CountAsync(ct),
                await _db.Airlines.CountAsync(a => a.IsVerified, ct),
                await _db.Airlines
                    .Where(a => a.SourceType != SeedSourceType && a.FirstSeenAt >= fromUtc && a.FirstSeenAt < toUtc)
                    .Select(a => a.FirstSeenAt)
                    .ToListAsync(ct)),

            new("airports", nameof(Airport.FirstSeenAt),
                await _db.Airports.CountAsync(ct),
                await _db.Airports.CountAsync(a => a.IsVerified, ct),
                await _db.Airports
                    .Where(a => a.SourceType != SeedSourceType && a.FirstSeenAt >= fromUtc && a.FirstSeenAt < toUtc)
                    .Select(a => a.FirstSeenAt)
                    .ToListAsync(ct)),

            // A null CreatedUtc (a row from before #334, or a seeded one) fails both comparisons.
            new("lounges", nameof(Lounge.CreatedUtc),
                await _db.Lounges.CountAsync(ct),
                await _db.Lounges.CountAsync(l => l.IsVerified, ct),
                await _db.Lounges
                    .Where(l => l.CreatedUtc >= fromUtc && l.CreatedUtc < toUtc)
                    .Select(l => l.CreatedUtc!.Value)
                    .ToListAsync(ct)),

            new("loyalty-programs", nameof(LoyaltyProgram.CreatedUtc),
                await _db.LoyaltyPrograms.CountAsync(ct),
                await _db.LoyaltyPrograms.CountAsync(p => p.IsVerified, ct),
                await _db.LoyaltyPrograms
                    .Where(p => p.CreatedUtc >= fromUtc && p.CreatedUtc < toUtc)
                    .Select(p => p.CreatedUtc!.Value)
                    .ToListAsync(ct)),
        ];
    }
}
