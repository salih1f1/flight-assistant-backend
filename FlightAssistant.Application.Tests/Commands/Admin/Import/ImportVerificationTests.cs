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
        Assert.Equal("prior-admin", entity.VerifiedBy);
        Assert.Equal("manual", entity.VerificationSource);
        Assert.Null(entity.VerificationIssue);
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
