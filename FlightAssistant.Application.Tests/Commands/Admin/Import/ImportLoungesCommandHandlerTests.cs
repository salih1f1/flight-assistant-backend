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

public class ImportLoungesCommandHandlerTests
{
    private readonly ILoungeRepository _loungeRepo = Substitute.For<ILoungeRepository>();
    private readonly IRefDataRepository _refRepo = Substitute.For<IRefDataRepository>();
    private readonly IAdminContext _adminContext = new AdminContext { ActorId = "importing-admin" };

    private ImportLoungesCommandHandler Handler() => new(_loungeRepo, _refRepo, _adminContext);

    [Fact]
    public async Task Handle_NewLounge_ResolvesAirportAndProviderThenInserts()
    {
        _refRepo.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(10);
        _loungeRepo.FindProviderByCodeAsync("PLAZA", Arg.Any<CancellationToken>()).Returns(LoungeProvider.Create("PLAZA", "Plaza Premium", LoungeProviderKind.Contract));
        _loungeRepo.FindByNaturalKeyWithProvidersAsync(10, "Plaza Premium Lounge", null, Arg.Any<CancellationToken>()).ReturnsNull();
        Lounge? added = null;
        await _loungeRepo.AddAsync(Arg.Do<Lounge>(l => added = l), Arg.Any<CancellationToken>());

        var handler = Handler();
        var dto = new LoungeImportDto("Plaza Premium Lounge", "LHR", null, null, null, true, false, false, null,
            [new LoungeProviderLinkImportDto("PLAZA", "Operator", null, null)]);

        var result = await handler.Handle(new ImportLoungesCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        Assert.NotNull(added);
    }

    [Fact]
    public async Task Handle_UnknownAirportIata_AddsErrorAndSkipsRecord()
    {
        _refRepo.GetAirportIdByIataAsync("ZZZ", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var dto = new LoungeImportDto("Some Lounge", "ZZZ", null, null, null, true, false, false, null, []);

        var result = await handler.Handle(new ImportLoungesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Single(result.Errors);
        Assert.Contains("ZZZ", result.Errors[0]);
    }

    [Fact]
    public async Task Handle_ExistingLounge_ReplacesProvidersAndUpdates()
    {
        var existingLounge = Lounge.Create(10, "Plaza Premium Lounge");
        _refRepo.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(10);
        _loungeRepo.FindByNaturalKeyWithProvidersAsync(10, "Plaza Premium Lounge", "T1", Arg.Any<CancellationToken>()).Returns(existingLounge);
        _loungeRepo.FindProviderByCodeAsync("PLAZA", Arg.Any<CancellationToken>()).Returns(LoungeProvider.Create("PLAZA", "Plaza Premium", LoungeProviderKind.Contract));

        var handler = Handler();
        var dto = new LoungeImportDto("Plaza Premium Lounge", "LHR", "T1", null, null, true, false, false, null,
            [new LoungeProviderLinkImportDto("PLAZA", "Operator", null, null)]);

        var result = await handler.Handle(new ImportLoungesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        await _loungeRepo.DidNotReceive().AddAsync(Arg.Any<Lounge>(), Arg.Any<CancellationToken>());
        await _loungeRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── #362: import verifies rows (spec §7 reverses the #355-era "stays unverified" rule) ──

    [Fact]
    public async Task Handle_NewLounge_NoVerificationValues_EndsVerifiedAsImportByActor()
    {
        _refRepo.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(10);
        _loungeRepo.FindByNaturalKeyWithProvidersAsync(10, "Plaza Premium Lounge", null, Arg.Any<CancellationToken>()).ReturnsNull();
        Lounge? added = null;
        await _loungeRepo.AddAsync(Arg.Do<Lounge>(l => added = l), Arg.Any<CancellationToken>());

        var handler = Handler();
        var dto = new LoungeImportDto("Plaza Premium Lounge", "LHR", null, null, null, true, false, false, null, []);

        await handler.Handle(new ImportLoungesCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.True(added!.IsVerified);
        Assert.Equal("importing-admin", added.VerifiedBy);
        Assert.Equal(VerificationSources.Import, added.VerificationSource);
    }

    [Fact]
    public async Task Handle_NewLounge_AllVerificationValuesPresent_KeepsThemAsIs()
    {
        _refRepo.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(10);
        _loungeRepo.FindByNaturalKeyWithProvidersAsync(10, "Plaza Premium Lounge", null, Arg.Any<CancellationToken>()).ReturnsNull();
        Lounge? added = null;
        await _loungeRepo.AddAsync(Arg.Do<Lounge>(l => added = l), Arg.Any<CancellationToken>());

        var handler = Handler();
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var dto = new LoungeImportDto("Plaza Premium Lounge", "LHR", null, null, null, true, false, false, null, [],
            verifiedAt, "prior-admin", "manual");

        await handler.Handle(new ImportLoungesCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.Equal(verifiedAt, added!.VerifiedAt);
        Assert.Equal("prior-admin", added.VerifiedBy);
        Assert.Equal("manual", added.VerificationSource);
    }

    [Fact]
    public async Task Handle_PartiallyFilledVerificationRow_IsRejected_WhileOtherRowInSameFileImports()
    {
        _refRepo.GetAirportIdByIataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(10);
        _loungeRepo.FindByNaturalKeyWithProvidersAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).ReturnsNull();

        var handler = Handler();
        var partial = new LoungeImportDto("Partial Lounge", "LHR", null, null, null, true, false, false, null, [],
            VerifiedBy: "prior-admin"); // VerifiedAt/VerificationSource missing
        var valid = new LoungeImportDto("Valid Lounge", "LHR", null, null, null, true, false, false, null, []);

        var result = await handler.Handle(new ImportLoungesCommand([partial, valid]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Single(result.Errors);
        Assert.Contains("Partial Lounge", result.Errors[0]);
        await _loungeRepo.DidNotReceive().AddAsync(
            Arg.Is<Lounge>(l => l.Name == "Partial Lounge"), Arg.Any<CancellationToken>());
        await _loungeRepo.Received(1).AddAsync(
            Arg.Is<Lounge>(l => l.Name == "Valid Lounge"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingLoungeInNeedsAttention_IsVerifiedAndIssueCleared()
    {
        var existingLounge = Lounge.Create(10, "Plaza Premium Lounge");
        existingLounge.MarkVerificationFailed("stale seed data");
        _refRepo.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(10);
        _loungeRepo.FindByNaturalKeyWithProvidersAsync(10, "Plaza Premium Lounge", null, Arg.Any<CancellationToken>()).Returns(existingLounge);

        var handler = Handler();
        var dto = new LoungeImportDto("Plaza Premium Lounge", "LHR", null, null, null, true, false, false, null, []);

        await handler.Handle(new ImportLoungesCommand([dto]), CancellationToken.None);

        Assert.True(existingLounge.IsVerified);
        Assert.Null(existingLounge.VerificationIssue);
        Assert.Equal(VerificationSources.Import, existingLounge.VerificationSource);
    }

    // ── Final-review fix wave: a lounge with a dropped provider link must not end verified ──

    [Fact]
    public async Task Handle_NewLounge_UnknownProviderRole_IsWrittenButNotVerified()
    {
        _refRepo.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(10);
        _loungeRepo.FindByNaturalKeyWithProvidersAsync(10, "Plaza Premium Lounge", null, Arg.Any<CancellationToken>()).ReturnsNull();
        Lounge? added = null;
        await _loungeRepo.AddAsync(Arg.Do<Lounge>(l => added = l), Arg.Any<CancellationToken>());

        var handler = Handler();
        // "NotARole" doesn't parse as a LoungeProviderRole, so this one link gets dropped — the
        // lounge is still written (with fewer providers than the CSV asked for), but must not end
        // verified: it did not get everything the row asked for.
        var dto = new LoungeImportDto("Plaza Premium Lounge", "LHR", null, null, null, true, false, false, null,
            [new LoungeProviderLinkImportDto("PLAZA", "NotARole", null, null)]);

        var result = await handler.Handle(new ImportLoungesCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Single(result.Errors);
        Assert.Contains("NotARole", result.Errors[0]);
        Assert.NotNull(added);
        Assert.False(added!.IsVerified);
        Assert.Null(added.VerifiedAt);
        Assert.Null(added.VerifiedBy);
        Assert.Null(added.VerificationSource);
        Assert.Null(added.VerificationIssue);
    }

    [Fact]
    public async Task Handle_ExistingVerifiedLounge_UnknownProviderCode_UpdatesButUnverifies()
    {
        var existingLounge = Lounge.Create(10, "Plaza Premium Lounge");
        existingLounge.Verify("prior-admin", "manual"); // starts verified, with real history to lose
        _refRepo.GetAirportIdByIataAsync("LHR", Arg.Any<CancellationToken>()).Returns(10);
        _loungeRepo.FindByNaturalKeyWithProvidersAsync(10, "Plaza Premium Lounge", null, Arg.Any<CancellationToken>()).Returns(existingLounge);
        _loungeRepo.FindProviderByCodeAsync("UNKNOWN", Arg.Any<CancellationToken>()).ReturnsNull();

        var handler = Handler();
        var dto = new LoungeImportDto("Plaza Premium Lounge", "LHR", null, null, null, true, false, false, null,
            [new LoungeProviderLinkImportDto("UNKNOWN", "Operator", null, null)]);

        var result = await handler.Handle(new ImportLoungesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Single(result.Errors);
        Assert.Contains("UNKNOWN", result.Errors[0]);
        // Re-importing with an unresolvable provider must not let the lounge coast on its old
        // verification — that would be exactly the borrowed-trust problem #362 exists to prevent.
        Assert.False(existingLounge.IsVerified);
        Assert.Null(existingLounge.VerifiedAt);
        Assert.Null(existingLounge.VerifiedBy);
        Assert.Null(existingLounge.VerificationSource);
        Assert.Null(existingLounge.VerificationIssue);
    }
}
