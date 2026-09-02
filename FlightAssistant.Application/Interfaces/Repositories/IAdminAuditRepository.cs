using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IAdminAuditRepository
    {
        /// <summary>
        /// Newest-first by <c>ActionUtc</c> (matches the index Task 1 created). All filters are
        /// optional and combine with AND. <paramref name="total"/> in the returned tuple reflects
        /// the count after every filter is applied, not the table count.
        /// </summary>
        Task<(IReadOnlyList<AdminAuditLog> Items, int Total)> SearchAsync(
            int page,
            int pageSize,
            string? entityType,
            string? entityId,
            string? actorId,
            AdminAuditAction? action,
            DateTime? fromUtc,
            DateTime? toUtc,
            CancellationToken ct = default);
    }
}
