using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

/// <summary>
/// Unit tests for the shared keep/stamp/reject decision (#362) all 8 import handlers delegate to,
/// isolated from any repository or CSV concern. <see cref="Alliance"/> stands in for "some
/// <c>IVerifiable</c> entity" — nothing here is alliance-specific.
/// </summary>
public class ImportVerificationTests
{
    private static Alliance NewEntity() => Alliance.Create("SA", "Star Alliance");

    [Fact]
    public void Resolve_AllThreePresent_KeepsThemAsIs()
    {
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var outcome = ImportVerification.Resolve(verifiedAt, "prior-admin", "manual", actorId: "importing-admin");

        Assert.False(outcome.IsRejected);
        Assert.Null(outcome.RejectionReason);

        var entity = NewEntity();
        outcome.Apply(entity);

        Assert.True(entity.IsVerified);
        Assert.Equal(verifiedAt, entity.VerifiedAt);
        Assert.Equal(DateTimeKind.Utc, entity.VerifiedAt!.Value.Kind);
        Assert.Equal("prior-admin", entity.VerifiedBy);
        Assert.Equal("manual", entity.VerificationSource);
        Assert.Null(entity.VerificationIssue);
    }

    /// <summary>
    /// The invariant this helper owns rather than trusts every caller to have already satisfied
    /// (#362 fix round 1): a caller other than the CSV parser — any future non-CSV caller of the 8
    /// import commands — could hand <see cref="Resolve"/> a <see cref="DateTimeKind.Local"/> value.
    /// Npgsql accepts it (a real instant), so this only proves the *conversion*; the "would 500
    /// outside the try" consequence a raw Local/Unspecified value causes is proved by the Postgres
    /// test instead, which is the one place that failure actually manifests.
    /// </summary>
    [Fact]
    public void Resolve_AllThreePresent_LocalKind_ConvertsToUtcAndKeepsTheSameInstant()
    {
        var verifiedAtLocal = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
        var expectedUtc = verifiedAtLocal.ToUniversalTime();

        var outcome = ImportVerification.Resolve(verifiedAtLocal, "prior-admin", "manual", actorId: "importing-admin");

        var entity = NewEntity();
        outcome.Apply(entity);

        Assert.Equal(DateTimeKind.Utc, entity.VerifiedAt!.Value.Kind);
        Assert.Equal(expectedUtc, entity.VerifiedAt!.Value);
    }

    /// <summary>
    /// <see cref="DateTimeKind.Unspecified"/> has no zone to convert *from*, so it is reinterpreted
    /// as UTC (same clock reading, <c>Kind</c> corrected) rather than converted — matching exactly
    /// what <c>CsvImportParser</c>'s own <c>DateTimeStyles.AssumeUniversal</c> does for the CSV path.
    /// </summary>
    [Fact]
    public void Resolve_AllThreePresent_UnspecifiedKind_IsReinterpretedAsUtc()
    {
        var verifiedAtUnspecified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);

        var outcome = ImportVerification.Resolve(verifiedAtUnspecified, "prior-admin", "manual", actorId: "importing-admin");

        var entity = NewEntity();
        outcome.Apply(entity);

        Assert.Equal(DateTimeKind.Utc, entity.VerifiedAt!.Value.Kind);
        Assert.Equal(DateTime.SpecifyKind(verifiedAtUnspecified, DateTimeKind.Utc), entity.VerifiedAt!.Value);
        // Same clock reading as the input, not a converted one — reinterpreted, not shifted.
        Assert.Equal(verifiedAtUnspecified.Ticks, entity.VerifiedAt!.Value.Ticks);
    }

    [Fact]
    public void Resolve_NonePresent_StampsImportSourceWithActorAndNow()
    {
        var before = DateTime.UtcNow;

        var outcome = ImportVerification.Resolve(null, null, null, actorId: "importing-admin");

        Assert.False(outcome.IsRejected);
        var entity = NewEntity();
        outcome.Apply(entity);

        Assert.True(entity.IsVerified);
        Assert.NotNull(entity.VerifiedAt);
        Assert.True(entity.VerifiedAt >= before);
        Assert.Equal("importing-admin", entity.VerifiedBy);
        Assert.Equal(VerificationSources.Import, entity.VerificationSource);
        Assert.Null(entity.VerificationIssue);
    }

    [Fact]
    public void Resolve_NonePresent_NullActorId_StampsAdminFallback()
    {
        var outcome = ImportVerification.Resolve(null, null, null, actorId: null);

        var entity = NewEntity();
        outcome.Apply(entity);

        Assert.Equal("admin", entity.VerifiedBy);
        Assert.Equal(VerificationSources.Import, entity.VerificationSource);
    }

    [Fact]
    public void Resolve_OnlyVerifiedAtPresent_RejectsNamingTheOtherTwo()
    {
        var outcome = ImportVerification.Resolve(DateTime.UtcNow, null, null, actorId: "importing-admin");

        Assert.True(outcome.IsRejected);
        Assert.Contains("VerifiedBy", outcome.RejectionReason);
        Assert.Contains("VerificationSource", outcome.RejectionReason);
        Assert.DoesNotContain("VerifiedAt,", outcome.RejectionReason);
    }

    [Fact]
    public void Resolve_OnlyVerifiedByPresent_RejectsNamingTheOtherTwo()
    {
        var outcome = ImportVerification.Resolve(null, "admin@example.com", null, actorId: "importing-admin");

        Assert.True(outcome.IsRejected);
        Assert.Contains("VerifiedAt", outcome.RejectionReason);
        Assert.Contains("VerificationSource", outcome.RejectionReason);
    }

    [Fact]
    public void Resolve_OnlyVerificationSourcePresent_RejectsNamingTheOtherTwo()
    {
        var outcome = ImportVerification.Resolve(null, null, "manual", actorId: "importing-admin");

        Assert.True(outcome.IsRejected);
        Assert.Contains("VerifiedAt", outcome.RejectionReason);
        Assert.Contains("VerifiedBy", outcome.RejectionReason);
    }
}
