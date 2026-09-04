using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Domain.Administration.Enums;

namespace FlightAssistant.Application.Administration
{
    /// <summary>
    /// Concrete, mutable <see cref="IAdminContext"/> registered scoped in DI (see
    /// <c>FlightAssistant.Application.DependencyInjection.AddApplication</c>). The <c>/admin</c>
    /// endpoint filter (<c>Program.cs</c>) populates <see cref="ActorId"/>/<see cref="ActorName"/>
    /// from the authenticated admin's claims, and <see cref="CorrelationId"/> (#319) from
    /// <c>HttpContext.Items</c>, for the lifetime of the request; the audit interceptor and the
    /// reveal handler then only ever read it back.
    /// </summary>
    public sealed class AdminContext : IAdminContext
    {
        public string? ActorId { get; set; }
        public string? ActorName { get; set; }
        public string? CorrelationId { get; set; }
        public AdminAuditAction? ActionOverride { get; set; }
    }
}
