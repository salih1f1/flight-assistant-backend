using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class FlightRepository : IFlightRepository
    {
        private readonly FlightAssistantDbContext _db;
        public FlightRepository(FlightAssistantDbContext db) => _db = db;

        // Generic IRepository<Flight> — Flight uses int PK, not Guid; use typed methods below instead.
        public Task<Flight?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException("Flight uses int PK. Use GetFlightByIdAsync instead.");

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

        public async Task<IReadOnlyList<(TravelerFlight Tracking, Flight Flight)>> GetTravelerFlightsAsync(Guid travelerId, CancellationToken ct = default)
        {
            var rows = await _db.TravelerFlights
                .Where(tf => tf.TravelerId == travelerId)
                .Join(_db.Flights, tf => tf.FlightId, f => f.Id, (tf, f) => new { tf, f })
                .OrderByDescending(x => x.f.ServiceDateUtc)
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

        public async Task AddFlightStatusEventAsync(FlightStatusEvent evt, CancellationToken ct = default) =>
            await _db.FlightStatusEvents.AddAsync(evt, ct);

        public Task<bool> FlightStatusEventExistsAsync(string dedupHash, CancellationToken ct = default) =>
            _db.FlightStatusEvents.AnyAsync(e => e.DedupHash == dedupHash, ct);

        public Task<int?> GetFlightStatusIdByCodeAsync(string code, CancellationToken ct = default) =>
            _db.FlightStatus
                .Where(s => s.Code == code)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync(ct);
    }
}
