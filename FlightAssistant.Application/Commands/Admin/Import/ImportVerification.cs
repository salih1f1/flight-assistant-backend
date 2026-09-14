using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Application.Commands.Admin.Import;

/// <summary>
/// Decides, for one imported row's three optional verification columns
/// (<c>VerifiedAt</c>/<c>VerifiedBy</c>/<c>VerificationSource</c>), whether to keep the CSV's
/// values as-is, stamp <see cref="VerificationSources.Import"/>, or reject the row — the #362 rule
/// from the auto-verification design (spec §6):
/// <list type="bullet">
/// <item>all three present → kept, with <c>VerifiedAt</c> normalised to UTC (see below) — the CSV
/// parser (<c>CsvImportParser</c>) already produces <see cref="DateTimeKind.Utc"/>, but this
/// helper is called by all 8 handlers directly, not only from behind that parser, so it owns the
/// guarantee itself rather than trusting every caller to have parsed correctly upstream.</item>
/// <item>none present (older export, or the columns absent entirely) → stamped with the importing
/// admin (<paramref name="actorId"/>, falling back to <c>"admin"</c> when unset — the same fallback
/// <c>AdminVerifyEndpoints</c> uses), <see cref="VerificationSources.Import"/>, and now (UTC).</item>
/// <item>only some present → rejected. A partial mix is a malformed row, not a row an importer
/// should get to silently mark verified or silently discard verification metadata from.</item>
/// </list>
/// All 8 import handlers call this rather than each re-implementing the decision.
/// </summary>
public static class ImportVerification
{
    public static ImportVerificationOutcome Resolve(
        DateTime? verifiedAt, string? verifiedBy, string? verificationSource, string? actorId)
    {
        var missing = new List<string>(3);
        if (!verifiedAt.HasValue) missing.Add("VerifiedAt");
        if (verifiedBy is null) missing.Add("VerifiedBy");
        if (verificationSource is null) missing.Add("VerificationSource");

        if (missing.Count == 0)
            return ImportVerificationOutcome.Keep(NormalizeToUtc(verifiedAt!.Value), verifiedBy!, verificationSource!);

        if (missing.Count == 3)
            return ImportVerificationOutcome.Stamp(actorId ?? "admin");

        return ImportVerificationOutcome.Reject(
            $"partial verification data — missing {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Guarantees a UTC <see cref="DateTime"/> reaches <see cref="IVerifiable.Verify(string, string, DateTime)"/>
    /// regardless of the <see cref="DateTime.Kind"/> a caller handed <see cref="Resolve"/> — Npgsql
    /// refuses <see cref="DateTimeKind.Unspecified"/> against a <c>timestamptz</c> column outright
    /// (the bug class CLAUDE.md documents for date query parameters), and that failure would
    /// otherwise surface at <c>SaveChangesAsync</c>, outside every handler's per-row <c>try</c>,
    /// turning one bad row into a 500 for the whole file. <see cref="DateTimeKind.Local"/> is
    /// converted (a real instant, just expressed in the wrong zone); <see cref="DateTimeKind.Unspecified"/>
    /// is reinterpreted as UTC rather than converted — there is no zone to convert *from* — matching
    /// exactly what <c>CsvImportParser</c>'s own <c>DateTimeStyles.AssumeUniversal</c> does for the
    /// CSV path. A row is never rejected for this; the value is coerced, not refused.
    /// </summary>
    private static DateTime NormalizeToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

/// <summary>The result of <see cref="ImportVerification.Resolve"/>: either a rejection reason, or
/// an action to apply to the row's <see cref="IVerifiable"/> once it has been inserted/updated.</summary>
public sealed class ImportVerificationOutcome
{
    public bool IsRejected { get; }
    public string? RejectionReason { get; }

    private readonly Action<IVerifiable>? _apply;

    private ImportVerificationOutcome(bool isRejected, string? rejectionReason, Action<IVerifiable>? apply)
    {
        IsRejected = isRejected;
        RejectionReason = rejectionReason;
        _apply = apply;
    }

    internal static ImportVerificationOutcome Keep(DateTime verifiedAtUtc, string verifiedBy, string source) =>
        new(false, null, e => e.Verify(verifiedBy, source, verifiedAtUtc));

    internal static ImportVerificationOutcome Stamp(string actorId) =>
        new(false, null, e => e.Verify(actorId, VerificationSources.Import, DateTime.UtcNow));

    internal static ImportVerificationOutcome Reject(string reason) => new(true, reason, null);

    /// <summary>Applies the resolved verification state to <paramref name="entity"/>. Must not be
    /// called when <see cref="IsRejected"/> is true.</summary>
    public void Apply(IVerifiable entity) => _apply!(entity);
}
