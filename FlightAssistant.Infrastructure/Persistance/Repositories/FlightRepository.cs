using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class FlightRepository : IFlightRepository
    {
        private readonly FlightAssistantDbContext _db;
        private readonly TimeProvider _timeProvider;
        private readonly int _statusWindowFutureDays;

        public FlightRepository(
            FlightAssistantDbContext db,
            TimeProvider timeProvider,
            IOptions<AeroApiOptions> aeroApiOptions)
        {
            _db = db;
            _timeProvider = timeProvider;
            _statusWindowFutureDays = aeroApiOptions.Value.StatusWindowFutureDays;
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
                .OrderByDescending(x => x.f.ServiceDateUtc);

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

        /// <summary>
        /// Candidates for <c>FlightStatusSyncBackgroundService</c> to poll. Bounded to the window
        /// <c>AeroApiFlightStatusProvider</c>'s <c>GET /flights/{ident}</c> call can actually answer
        /// for (issue #283) — without an upper bound, a flight tracked months ahead (the search
        /// horizon reaches <c>AeroApi:MaxFutureMonths</c>, up to 12) got polled every sync cycle,
        /// forever, always returning null, at metered-API cost.
        ///
        /// <para>
        /// The lower bound stays exactly "today" on purpose: widening it to include yesterday's
        /// departures would increase spend, the opposite of what this method exists to fix. (A
        /// flight that departed yesterday can as a result never reach a final ARR status here — a
        /// real gap, but a separate concern from this one; not addressed by this method.)
        /// </para>
        ///
        /// <para>
        /// <b>The pad.</b> <see cref="Flight.ServiceDateUtc"/> is the origin-local service date, not
        /// a UTC instant (see CLAUDE.md's "ServiceDate is origin-local everywhere"), but it is
        /// compared below against a plain UTC calendar date. The two can disagree by up to a day at
        /// the window edge — an origin far enough east of UTC reports a local date that is already
        /// "tomorrow" in UTC terms. The upper bound is therefore padded by one extra day beyond
        /// <see cref="AeroApiOptions.StatusWindowFutureDays"/> so that a flight AeroAPI could still
        /// answer for is never silently dropped from polling — a wasted query at the boundary is far
        /// cheaper than a traveller who stops receiving status updates.
        /// </para>
        /// </summary>
        public async Task<IReadOnlyList<Flight>> GetActiveTrackedFlightsAsync(CancellationToken ct = default)
        {
            var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
            var upperBound = today.AddDays(_statusWindowFutureDays + 1);

            return await _db.Flights
                .Where(f => f.ServiceDateUtc >= today &&
                            f.ServiceDateUtc <= upperBound &&
                            _db.TravelerFlights.Any(tf => tf.FlightId == f.Id))
                .ToListAsync(ct);
        }
    }
}
