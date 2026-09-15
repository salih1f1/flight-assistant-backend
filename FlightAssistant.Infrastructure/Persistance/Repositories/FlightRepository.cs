using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class FlightRepository : IFlightRepository
    {
        private readonly FlightAssistantDbContext _db;
        private readonly TimeProvider _timeProvider;

        public FlightRepository(
            FlightAssistantDbContext db,
            TimeProvider timeProvider)
        {
            _db = db;
            _timeProvider = timeProvider;
        }

        public async Task AddAsync(Flight entity, CancellationToken ct = default) =>
            await _db.Flights.AddAsync(entity, ct);

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            _db.SaveChangesAsync(ct);

        // --- Domain-specific queries ---

        public Task<Flight?> GetFlightByIdAsync(int flightId, CancellationToken ct = default) =>
            _db.Flights.FirstOrDefaultAsync(f => f.Id == flightId, ct);

        public Task<Flight?> GetByFlightNumberAndDateAsync(int airlineId, string flightNumber, DateOnly serviceDate, CancellationToken ct = default) =>
            _db.Flights.FirstOrDefaultAsync(
                f => f.AirlineId == airlineId && f.FlightNumber == flightNumber && f.ServiceDateUtc == serviceDate,
                ct);

        public async Task<Flight> GetOrCreateFlightAsync(Flight candidate, CancellationToken ct = default)
        {
            var existing = await GetByFlightNumberAndDateAsync(candidate.AirlineId, candidate.FlightNumber, candidate.ServiceDateUtc, ct);
            if (existing is not null) return existing;

            await _db.Flights.AddAsync(candidate, ct);
            try
            {
                await _db.SaveChangesAsync(ct);
                return candidate;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Lost the race — detach our failed insert and return the row the winner created.
                _db.Entry(candidate).State = EntityState.Detached;
                var winner = await GetByFlightNumberAndDateAsync(candidate.AirlineId, candidate.FlightNumber, candidate.ServiceDateUtc, ct);
                if (winner is null) throw;
                return winner;
            }
        }

        public async Task<(IReadOnlyList<(TravelerFlight Tracking, Flight Flight)> Items, int Total)> GetTravelerFlightsAsync(Guid travelerId, int page, int pageSize, CancellationToken ct = default)
        {
            var query = _db.TravelerFlights
                .Where(tf => tf.TravelerId == travelerId)
                .Join(_db.Flights, tf => tf.FlightId, f => f.Id, (tf, f) => new { tf, f })
                .OrderByDescending(x => x.f.ServiceDateUtc)
                // Id tiebreaker: ServiceDateUtc is not unique, and paging on a tie can repeat or skip rows (#328).
                .ThenBy(x => x.f.Id);

            var total = await query.CountAsync(ct);
            var rows = await query.Page(page, pageSize).ToListAsync(ct);
            return (rows.Select(x => (x.tf, x.f)).ToList(), total);
        }

        public async Task<IReadOnlyList<(TravelerFlight Tracking, Flight Flight)>> GetRecentTravelerFlightsAsync(Guid travelerId, int limit, CancellationToken ct = default)
        {
            var rows = await _db.TravelerFlights
                .AsNoTracking()
                .Where(tf => tf.TravelerId == travelerId)
                .Join(_db.Flights.AsNoTracking(), tf => tf.FlightId, f => f.Id, (tf, f) => new { tf, f })
                .OrderByDescending(x => x.f.ServiceDateUtc)
                .Take(limit)
                .ToListAsync(ct);
            return rows.Select(x => (x.tf, x.f)).ToList();
        }

        public Task<TravelerFlight?> GetTravelerFlightAsync(Guid travelerId, int flightId, CancellationToken ct = default) =>
            _db.TravelerFlights.FirstOrDefaultAsync(tf => tf.TravelerId == travelerId && tf.FlightId == flightId, ct);

        public async Task AddTravelerFlightAsync(TravelerFlight tf, CancellationToken ct = default) =>
            await _db.TravelerFlights.AddAsync(tf, ct);

        public Task RemoveTravelerFlightAsync(TravelerFlight tf, CancellationToken ct = default)
        {
            _db.TravelerFlights.Remove(tf);
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<TravelerFlight>> GetTrackingsByFlightIdAsync(int flightId, CancellationToken ct = default) =>
            await _db.TravelerFlights.Where(tf => tf.FlightId == flightId).ToListAsync(ct);

        public async Task<IReadOnlyList<FlightStatusEvent>> GetFlightStatusEventsAsync(int flightId, CancellationToken ct = default) =>
            await _db.FlightStatusEvents
                .Where(e => e.FlightId == flightId)
                .Include(e => e.Status)
                .OrderByDescending(e => e.EffectiveAtUtc)
                .ToListAsync(ct);

        public async Task<IReadOnlyList<FlightStatusEvent>> GetRecentFlightStatusEventsAsync(int flightId, int limit, CancellationToken ct = default) =>
            await _db.FlightStatusEvents
                .AsNoTracking()
                .Where(e => e.FlightId == flightId)
                .Include(e => e.Status)
                .OrderByDescending(e => e.EffectiveAtUtc)
                .Take(limit)
                .ToListAsync(ct);

        public async Task AddFlightStatusEventAsync(FlightStatusEvent evt, CancellationToken ct = default) =>
            await _db.FlightStatusEvents.AddAsync(evt, ct);

        public Task<bool> FlightStatusEventExistsAsync(string dedupHash, CancellationToken ct = default) =>
            _db.FlightStatusEvents.AnyAsync(e => e.DedupHash == dedupHash, ct);

        public Task<int?> GetFlightStatusIdByCodeAsync(string code, CancellationToken ct = default) =>
            _db.FlightStatus
                .Where(s => s.Code == code)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync(ct);

        public async Task<IReadOnlyList<int>> GetDueTrackedFlightIdsAsync(CancellationToken ct = default)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // Any(), not a join: a flight tracked by several travelers is synced once. A tracked flight
            // with no mode was never planned (tracked by an older build, or a track racing the last
            // untrack) and would otherwise never be synced. PostgreSQL sorts nulls last, so
            // "unplanned first" is an explicit key.
            return await _db.Flights
                .Where(f => (f.SyncMode == null || f.NextSyncDueUtc <= now) &&
                            _db.TravelerFlights.Any(tf => tf.FlightId == f.Id))
                .OrderBy(f => f.SyncMode == null ? 0 : 1)
                .ThenBy(f => f.NextSyncDueUtc)
                .ThenBy(f => f.Id)
                .Select(f => f.Id)
                .ToListAsync(ct);
        }

        public Task<bool> IsFlightTrackedAsync(int flightId, CancellationToken ct = default) =>
            _db.TravelerFlights.AnyAsync(tf => tf.FlightId == flightId, ct);

        public Task<int> CountActiveTrackedFlightsAsync(
            Guid travelerId, DateTime nowUtc, TimeSpan stopAfterArrival, TimeSpan stopWithoutArrivalAfter,
            CancellationToken ct = default)
        {
            // The negation of FlightSyncPlanner's Stopped rule; FlightRepositoryTests checks the two agree.
            var stopBefore = nowUtc - stopAfterArrival;
            var stopWithoutArrivalBefore = nowUtc - stopWithoutArrivalAfter;

            return _db.TravelerFlights
                .Where(tf => tf.TravelerId == travelerId)
                .Join(_db.Flights, tf => tf.FlightId, f => f.Id, (tf, f) => f)
                .CountAsync(f => (f.ActualArrivalUtc == null || f.ActualArrivalUtc > stopBefore)
                                 && (f.CancelledAtUtc == null || f.CancelledAtUtc > stopBefore)
                                 && (f.ActualArrivalUtc != null
                                     || (f.EstimatedArrivalUtc ?? f.ScheduledArrivalUtc) > stopWithoutArrivalBefore), ct);
        }
    }
}
