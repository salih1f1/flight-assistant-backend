using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class AdminAuditRepository : IAdminAuditRepository
    {
        private readonly FlightAssistantDbContext _db;
        public AdminAuditRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<(IReadOnlyList<AdminAuditLog> Items, int Total)> SearchAsync(
            int page,
            int pageSize,
            string? entityType,
            string? entityId,
            string? actorId,
            AdminAuditAction? action,
            DateTime? fromUtc,
            DateTime? toUtc,
            CancellationToken ct = default)
        {
            var query = _db.AdminAuditLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(entityType))
                query = query.Where(a => a.EntityType == entityType);
            if (!string.IsNullOrWhiteSpace(entityId))
                query = query.Where(a => a.EntityId == entityId);
            if (!string.IsNullOrWhiteSpace(actorId))
                query = query.Where(a => a.ActorId == actorId);
            if (action.HasValue)
                query = query.Where(a => a.Action == action.Value);
            if (fromUtc.HasValue)
                query = query.Where(a => a.ActionUtc >= fromUtc.Value);
            if (toUtc.HasValue)
                query = query.Where(a => a.ActionUtc <= toUtc.Value);

            // Total must be counted after every filter above, before paging — this is the classic
            // bug in this shape (a Total computed against the unfiltered table).
            var total = await query.CountAsync(ct);

            var items = await query
                .OrderByDescending(a => a.ActionUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            return (items, total);
        }
    }
}
