using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Interfaces;

namespace FlightAssistant.Application.Commands.Admin.Import;

/// <summary>
/// Decides, for one imported row's three optional verification columns
/// (<c>VerifiedAt</c>/<c>VerifiedBy</c>/<c>VerificationSource</c>), whether to keep the CSV's
/// values as-is, stamp <see cref="VerificationSources.Import"/>, or reject the row — the #362 rule
/// from the auto-verification design (spec §6):
/// <list type="bullet">
/// <item>all three present → kept exactly as the CSV carried them.</item>
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
            return ImportVerificationOutcome.Keep(verifiedAt!.Value, verifiedBy!, verificationSource!);

        if (missing.Count == 3)
            return ImportVerificationOutcome.Stamp(actorId ?? "admin");

        return ImportVerificationOutcome.Reject(
            $"partial verification data — missing {string.Join(", ", missing)}");
    }
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
