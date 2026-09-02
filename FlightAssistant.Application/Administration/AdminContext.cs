using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Domain.Administration.Enums;

namespace FlightAssistant.Application.Administration
{
    /// <summary>
    /// Concrete, mutable <see cref="IAdminContext"/> registered scoped in DI (see
    /// <c>FlightAssistant.Application.DependencyInjection.AddApplication</c>). Something earlier
    /// in the pipeline — an endpoint filter or middleware added in Task 2 — populates
    /// <see cref="ActorId"/>/<see cref="ActorName"/> from the authenticated admin's claims for
    /// the lifetime of the request; the audit interceptor then only ever reads it.
    /// </summary>
    public sealed class AdminContext : IAdminContext
    {
        public string? ActorId { get; set; }
        public string? ActorName { get; set; }
        public AdminAuditAction? ActionOverride { get; set; }
    }
}
