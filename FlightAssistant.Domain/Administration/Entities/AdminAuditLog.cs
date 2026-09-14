using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Exceptions;
using System.Diagnostics;

namespace FlightAssistant.Domain.Administration.Entities
{
    /// <summary>
    /// One row per admin write, recorded in the same database transaction as the write itself
    /// (see the EF interceptor added in Infrastructure by Task 2 of #313).
    ///
    /// Extends <see cref="BaseEntity"/>, not <see cref="AuditableEntity"/> — an audit row that
    /// audits itself is absurd, and <c>CreatedUtc</c> would just duplicate
    /// <see cref="ActionUtc"/>.
    ///
    /// Lives in <c>Domain</c> (a new <c>Administration</c> area, deliberately not under
    /// <c>Configuration</c> alongside <c>SeedVersion</c> — that folder is the reference-data
    /// bounded context, and an audit log is not reference data) only because the read-side query
    /// handler (Task 3) lives in Application, and Application cannot reference Infrastructure.
    ///
    /// Audit rows are append-only: no mutators beyond the factory method.
    /// </summary>
    public sealed class AdminAuditLog : BaseEntity
    {
        /// <summary>The <c>oid</c> claim of the admin who performed the action.</summary>
        public string ActorId { get; private set; } = default!;

        /// <summary>Display name of the actor, if known.</summary>
        public string? ActorName { get; private set; }

        public DateTime ActionUtc { get; private set; }

        public AdminAuditAction Action { get; private set; }

        /// <summary>CLR type name of the entity written.</summary>
        public string EntityType { get; private set; } = default!;

        /// <summary>
        /// The written entity's key, stringified. Keys are <c>Guid</c> on some entities and
        /// <c>int</c> on others; composite keys join their parts with <c>|</c>.
        ///
        /// <para>
        /// <c>null</c> on a <see cref="Domain.Administration.Enums.AdminAuditAction.Create"/> row
        /// whose entity uses a store-generated key (e.g. a Postgres identity column): the real
        /// value is assigned by the database during the very <c>SaveChanges</c> call this row is
        /// written inside, so it genuinely does not exist yet when the row is built. This is never
        /// blank/whitespace for any other action — <see cref="Create"/> still rejects that.
        /// </para>
        /// </summary>
        public string? EntityId { get; private set; }

        /// <summary>Short human-readable line, e.g. "Updated Airport LHR".</summary>
        public string Summary { get; private set; } = default!;

        /// <summary>Entity state before the write, serialized as JSON. Null for a create.</summary>
        public string? Before { get; private set; }

        /// <summary>Entity state after the write, serialized as JSON. Null for a delete.</summary>
        public string? After { get; private set; }

        /// <summary>
        /// The correlation id for the request that produced this write (#319) — in production, the
        /// same string <c>CorrelationIdMiddleware</c> put on the <c>X-Correlation-Id</c> response
        /// header and every Serilog line for that request — passed in explicitly by the caller (see
        /// <see cref="Create"/>). Falls back to <see cref="Activity.Current"/>'s id only when a
        /// caller has nothing else to pass.
        /// </summary>
        public string? TraceId { get; private set; }

        private AdminAuditLog() { } // EF

        /// <summary>
        /// Records one admin write. <paramref name="actionUtc"/> and <paramref name="traceId"/>
        /// default to "now" and the ambient <see cref="Activity.Current"/> id respectively when
        /// not supplied, mirroring how other factories in this codebase (e.g.
        /// <c>FlightStatusEvent.Create</c>) capture ambient state — but both are accepted as
        /// explicit parameters so a caller can pin them precisely, and so tests don't depend on
        /// wall-clock time or an ambient <see cref="Activity"/>. In production both writers
        /// (<c>AdminAuditInterceptor</c>, <c>RevealValueCommandHandler</c>) pass the request's
        /// correlation id explicitly (#319); the <see cref="Activity.Current"/> default here is
        /// what a caller with nothing else to offer falls back to, not the primary source.
        /// </summary>
        public static AdminAuditLog Create(
            string actorId,
            string? actorName,
            AdminAuditAction action,
            string entityType,
            string? entityId,
            string summary,
            string? before = null,
            string? after = null,
            string? traceId = null,
            DateTime? actionUtc = null)
        {
            if (string.IsNullOrWhiteSpace(actorId)) throw new DomainException("ActorId is required.");
            if (string.IsNullOrWhiteSpace(entityType)) throw new DomainException("EntityType is required.");
            // entityId may be null (store-generated key not yet assigned — see the property's
            // doc comment) but never blank/whitespace: that would be a caller bug, not the
            // legitimate "not known yet" case.
            if (entityId is not null && string.IsNullOrWhiteSpace(entityId)) throw new DomainException("EntityId must not be blank; pass null when the id is not yet known.");
            if (string.IsNullOrWhiteSpace(summary)) throw new DomainException("Summary is required.");

            return new AdminAuditLog
            {
                ActorId = actorId,
                ActorName = string.IsNullOrWhiteSpace(actorName) ? null : actorName,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Summary = summary,
                Before = before,
                After = after,
                TraceId = traceId ?? Activity.Current?.Id,
                ActionUtc = actionUtc ?? DateTime.UtcNow
            };
        }
    }
}
