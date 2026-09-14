using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportLoungeProvidersCommandHandlerTests
{
    private readonly ILoungeRepository _repo = Substitute.For<ILoungeRepository>();
    private readonly IAdminContext _adminContext = new AdminContext { ActorId = "importing-admin" };

    private ImportLoungeProvidersCommandHandler Handler() => new(_repo, _adminContext);

    [Fact]
    public async Task Handle_NewProvider_InsertsAndReturnsInsertedCount()
    {
        _repo.FindProviderByCodeAsync("AMEX", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var dto = new LoungeProviderImportDto("AMEX", "American Express", "Card", null);

        var result = await handler.Handle(new ImportLoungeProvidersCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddProviderAsync(Arg.Any<LoungeProvider>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingProvider_UpdatesAndReturnsUpdatedCount()
    {
        var existing = LoungeProvider.Create("AMEX", "Old Name", LoungeProviderKind.Card, null);
        _repo.FindProviderByCodeAsync("AMEX", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = Handler();
        var dto = new LoungeProviderImportDto("AMEX", "American Express", "Card", "Premium card lounge access");

        var result = await handler.Handle(new ImportLoungeProvidersCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddProviderAsync(Arg.Any<LoungeProvider>(), Arg.Any<CancellationToken>());
        Assert.Equal("American Express", existing.Name);
    }

    [Fact]
    public async Task Handle_InvalidKind_AddsErrorAndSkips()
    {
        var handler = Handler();
        var dto = new LoungeProviderImportDto("XYZ", "Unknown", "NotAValidKind", null);

        var result = await handler.Handle(new ImportLoungeProvidersCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Single(result.Errors);
        Assert.Contains("NotAValidKind", result.Errors[0]);
        await _repo.DidNotReceive().AddProviderAsync(Arg.Any<LoungeProvider>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── #362: import verifies rows ───────────────────────────────────────────

    [Fact]
    public async Task Handle_NewProvider_NoVerificationValues_EndsVerifiedAsImportByActor()
    {
        _repo.FindProviderByCodeAsync("AMEX", Arg.Any<CancellationToken>()).ReturnsNull();
        LoungeProvider? added = null;
        await _repo.AddProviderAsync(Arg.Do<LoungeProvider>(p => added = p), Arg.Any<CancellationToken>());
        var handler = Handler();
        var dto = new LoungeProviderImportDto("AMEX", "American Express", "Card", null);

        await handler.Handle(new ImportLoungeProvidersCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.True(added!.IsVerified);
        Assert.Equal("importing-admin", added.VerifiedBy);
        Assert.Equal(VerificationSources.Import, added.VerificationSource);
    }

    [Fact]
    public async Task Handle_NewProvider_AllVerificationValuesPresent_KeepsThemAsIs()
    {
        _repo.FindProviderByCodeAsync("AMEX", Arg.Any<CancellationToken>()).ReturnsNull();
        LoungeProvider? added = null;
        await _repo.AddProviderAsync(Arg.Do<LoungeProvider>(p => added = p), Arg.Any<CancellationToken>());
        var handler = Handler();
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var dto = new LoungeProviderImportDto("AMEX", "American Express", "Card", null,
            verifiedAt, "prior-admin", "manual");

        await handler.Handle(new ImportLoungeProvidersCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.Equal(verifiedAt, added!.VerifiedAt);
        Assert.Equal("prior-admin", added.VerifiedBy);
        Assert.Equal("manual", added.VerificationSource);
    }

    [Fact]
    public async Task Handle_PartiallyFilledVerificationRow_IsRejected_WhileOtherRowInSameFileImports()
    {
        _repo.FindProviderByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var partial = new LoungeProviderImportDto("AMEX", "American Express", "Card", null,
            VerifiedBy: "prior-admin"); // VerifiedAt/VerificationSource missing
        var valid = new LoungeProviderImportDto("PP", "Priority Pass", "Contract", null);

        var result = await handler.Handle(new ImportLoungeProvidersCommand([partial, valid]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Single(result.Errors);
        Assert.Contains("AMEX", result.Errors[0]);
        await _repo.DidNotReceive().AddProviderAsync(
            Arg.Is<LoungeProvider>(p => p.Code == "AMEX"), Arg.Any<CancellationToken>());
        await _repo.Received(1).AddProviderAsync(
            Arg.Is<LoungeProvider>(p => p.Code == "PP"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingProviderInNeedsAttention_IsVerifiedAndIssueCleared()
    {
        var existing = LoungeProvider.Create("AMEX", "Old Name", LoungeProviderKind.Card, null);
        existing.MarkVerificationFailed("stale seed data");
        _repo.FindProviderByCodeAsync("AMEX", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = Handler();
        var dto = new LoungeProviderImportDto("AMEX", "American Express", "Card", null);

        await handler.Handle(new ImportLoungeProvidersCommand([dto]), CancellationToken.None);

        Assert.True(existing.IsVerified);
        Assert.Null(existing.VerificationIssue);
        Assert.Equal(VerificationSources.Import, existing.VerificationSource);
    }
}
