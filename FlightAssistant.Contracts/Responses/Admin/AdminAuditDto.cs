using System.Text.Json;

namespace FlightAssistant.Contracts.Responses.Admin
{
    /// <summary>
    /// One row from the admin audit log (#313), newest first by default.
    ///
    /// <para>
    /// <b>Before/After.</b> The database stores these as <c>jsonb</c> text. This DTO carries them
    /// as <see cref="JsonElement"/>, not <c>string</c>, so System.Text.Json writes them into the
    /// response body as nested JSON objects the admin panel can render directly — a plain
    /// <c>string</c> field would come across JSON-escaped, a string-in-a-string. A null column maps
    /// to a null <c>JsonElement?</c> rather than <c>default(JsonElement)</c>: the latter's
    /// <c>ValueKind</c> is <c>Undefined</c>, which System.Text.Json refuses to serialize at all, so
    /// the mapping code must special-case a null column explicitly instead of just deserializing an
    /// empty string.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="EntityId"/> is not always populated.</b> It is null on a create of an entity
    /// with a store-generated key (e.g. a Postgres identity column) — the database assigns the real
    /// id during the very write this row records, so it does not exist yet at write time. Do not
    /// treat <c>EntityType</c>+<c>EntityId</c> filtering as a complete "history of this row" answer:
    /// such a create row is never matched by an <c>EntityId</c> filter, and is identified through
    /// <see cref="After"/> instead, which holds the created entity's full property set.
    /// </para>
    /// </summary>
    public sealed record AdminAuditDto(
        Guid Id,
        string ActorId,
        string? ActorName,
        DateTime ActionUtc,
        string Action,
        string EntityType,
        string? EntityId,
        string Summary,
        JsonElement? Before,
        JsonElement? After,
        string? TraceId
    );
}
