using FlightAssistant.Domain.Administration.Enums;

namespace FlightAssistant.Application.Interfaces.Administration
{
    /// <summary>
    /// Read-only view of the current admin actor, for the audit-log EF interceptor (Task 2 of
    /// #313, in Infrastructure) to stamp onto every <c>AdminAuditLog</c> row it writes.
    ///
    /// This lives in Application, not API — unlike <c>TravelerContext</c>, which lives in API
    /// because only API reads it. <see cref="IAdminContext"/> is read by an EF
    /// <c>SaveChangesInterceptor</c> in Infrastructure, and Infrastructure cannot reference API,
    /// so the abstraction has to sit one layer down from where <c>TravelerContext</c> does.
    /// </summary>
    public interface IAdminContext
    {
        /// <summary>The <c>oid</c> claim of the authenticated admin, or null if unset.</summary>
        string? ActorId { get; }

        /// <summary>Display name of the admin, if known.</summary>
        string? ActorName { get; }

        /// <summary>
        /// The request's correlation id (#319) — <c>CorrelationIdMiddleware</c>'s
        /// <c>X-Correlation-Id</c>, the same string already on the response header and every
        /// Serilog line for this request. Populated by the <c>/admin</c> endpoint filter
        /// (<c>Program.cs</c>) from <c>HttpContext.Items</c>, and read back by
        /// <c>AdminAuditInterceptor</c> and <c>RevealValueCommandHandler</c> to stamp
        /// <c>AdminAuditLog.TraceId</c> — the same reason <see cref="ActorId"/> is routed through
        /// here rather than read off <c>HttpContext</c> directly: neither Infrastructure nor this
        /// interface's own layer can reference API, where <c>HttpContext</c> lives.
        /// </summary>
        string? CorrelationId { get; }

        /// <summary>
        /// Overrides the <see cref="AdminAuditAction"/> the interceptor would otherwise infer
        /// from EF's own <c>EntityState</c> (Added/Modified/Deleted). Exists for exactly one
        /// caller — the CSV import endpoint (Task 2), whose writes are otherwise
        /// indistinguishable from an ordinary create/update — and stays null everywhere else.
        /// </summary>
        AdminAuditAction? ActionOverride { get; }
    }
}
