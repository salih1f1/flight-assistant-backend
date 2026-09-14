using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportLoyaltyProgramKindsCommandHandlerTests
{
    private readonly ILoyaltyRepository _repo = Substitute.For<ILoyaltyRepository>();
    private readonly IAdminContext _adminContext = new AdminContext { ActorId = "importing-admin" };

    private ImportLoyaltyProgramKindsCommandHandler Handler() => new(_repo, _adminContext);

    [Fact]
    public async Task Handle_NewKind_InsertsAndReturnsInsertedCount()
    {
        _repo.FindKindByCodeAsync("AIRLINE", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var dto = new LoyaltyProgramKindImportDto("AIRLINE", "Airline Miles", "Earn miles on flights");

        var result = await handler.Handle(new ImportLoyaltyProgramKindsCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddKindAsync(Arg.Any<LoyaltyProgramKind>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingKind_UpdatesAndReturnsUpdatedCount()
    {
        var existing = LoyaltyProgramKind.Create("AIRLINE", "Old Name", null);
        _repo.FindKindByCodeAsync("AIRLINE", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = Handler();
        var dto = new LoyaltyProgramKindImportDto("AIRLINE", "Airline Miles", "Updated description");

        var result = await handler.Handle(new ImportLoyaltyProgramKindsCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddKindAsync(Arg.Any<LoyaltyProgramKind>(), Arg.Any<CancellationToken>());
        Assert.Equal("Airline Miles", existing.Name);
    }

    // ── #362: import verifies rows ───────────────────────────────────────────

    [Fact]
    public async Task Handle_NewKind_NoVerificationValues_EndsVerifiedAsImportByActor()
    {
        _repo.FindKindByCodeAsync("AIRLINE", Arg.Any<CancellationToken>()).ReturnsNull();
        LoyaltyProgramKind? added = null;
        await _repo.AddKindAsync(Arg.Do<LoyaltyProgramKind>(k => added = k), Arg.Any<CancellationToken>());
        var handler = Handler();
        var dto = new LoyaltyProgramKindImportDto("AIRLINE", "Airline Miles", null);

        await handler.Handle(new ImportLoyaltyProgramKindsCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.True(added!.IsVerified);
        Assert.Equal("importing-admin", added.VerifiedBy);
        Assert.Equal(VerificationSources.Import, added.VerificationSource);
    }

    [Fact]
    public async Task Handle_NewKind_AllVerificationValuesPresent_KeepsThemAsIs()
    {
        _repo.FindKindByCodeAsync("AIRLINE", Arg.Any<CancellationToken>()).ReturnsNull();
        LoyaltyProgramKind? added = null;
        await _repo.AddKindAsync(Arg.Do<LoyaltyProgramKind>(k => added = k), Arg.Any<CancellationToken>());
        var handler = Handler();
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var dto = new LoyaltyProgramKindImportDto("AIRLINE", "Airline Miles", null,
            verifiedAt, "prior-admin", "manual");

        await handler.Handle(new ImportLoyaltyProgramKindsCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.Equal(verifiedAt, added!.VerifiedAt);
        Assert.Equal("prior-admin", added.VerifiedBy);
        Assert.Equal("manual", added.VerificationSource);
    }

    [Fact]
    public async Task Handle_PartiallyFilledVerificationRow_IsRejected_WhileOtherRowInSameFileImports()
    {
        _repo.FindKindByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var partial = new LoyaltyProgramKindImportDto("AIRLINE", "Airline Miles", null,
            VerifiedBy: "prior-admin"); // VerifiedAt/VerificationSource missing
        var valid = new LoyaltyProgramKindImportDto("HOTEL", "Hotel Points", null);

        var result = await handler.Handle(new ImportLoyaltyProgramKindsCommand([partial, valid]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Single(result.Errors);
        Assert.Contains("AIRLINE", result.Errors[0]);
        await _repo.DidNotReceive().AddKindAsync(
            Arg.Is<LoyaltyProgramKind>(k => k.Code == "AIRLINE"), Arg.Any<CancellationToken>());
        await _repo.Received(1).AddKindAsync(
            Arg.Is<LoyaltyProgramKind>(k => k.Code == "HOTEL"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingKindInNeedsAttention_IsVerifiedAndIssueCleared()
    {
        var existing = LoyaltyProgramKind.Create("AIRLINE", "Old Name", null);
        existing.MarkVerificationFailed("stale seed data");
        _repo.FindKindByCodeAsync("AIRLINE", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = Handler();
        var dto = new LoyaltyProgramKindImportDto("AIRLINE", "Airline Miles", null);

        await handler.Handle(new ImportLoyaltyProgramKindsCommand([dto]), CancellationToken.None);

        Assert.True(existing.IsVerified);
        Assert.Null(existing.VerificationIssue);
        Assert.Equal(VerificationSources.Import, existing.VerificationSource);
    }
}
