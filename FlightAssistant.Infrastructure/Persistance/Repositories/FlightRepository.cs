using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class FlightRepository : IFlightRepository
    {
        private readonly FlightAssistantDbContext _db;
        public FlightRepository(FlightAssistantDbContext db) => _db = db;

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

        public async Task<(IReadOnlyList<(TravelerFlight Tracking, Flight Flight)> Items, int Total)> GetTravelerFlightsAsync(Guid travelerId, int page, int pageSize, CancellationToken ct = default)
        {
            var query = _db.TravelerFlights
                .Where(tf => tf.TravelerId == travelerId)
                .Join(_db.Flights, tf => tf.FlightId, f => f.Id, (tf, f) => new { tf, f })
                .OrderByDescending(x => x.f.ServiceDateUtc);

            var total = await query.CountAsync(ct);
            var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return (rows.Select(x => (x.tf, x.f)).ToList(), total);
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

        public async Task AddFlightStatusEventAsync(FlightStatusEvent evt, CancellationToken ct = default) =>
            await _db.FlightStatusEvents.AddAsync(evt, ct);

        public Task<bool> FlightStatusEventExistsAsync(string dedupHash, CancellationToken ct = default) =>
            _db.FlightStatusEvents.AnyAsync(e => e.DedupHash == dedupHash, ct);

        public Task<int?> GetFlightStatusIdByCodeAsync(string code, CancellationToken ct = default) =>
            _db.FlightStatus
                .Where(s => s.Code == code)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync(ct);

        public async Task<IReadOnlyList<Flight>> GetActiveTrackedFlightsAsync(CancellationToken ct = default)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            return await _db.Flights
                .Where(f => f.ServiceDateUtc >= today &&
                            _db.TravelerFlights.Any(tf => tf.FlightId == f.Id && tf.Notify))
                .ToListAsync(ct);
        }
    }
}
