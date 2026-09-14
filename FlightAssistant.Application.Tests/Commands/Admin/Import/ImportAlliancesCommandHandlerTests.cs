using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportAlliancesCommandHandlerTests
{
    private readonly IAllianceRepository _repo = Substitute.For<IAllianceRepository>();
    private readonly IAdminContext _adminContext = new AdminContext { ActorId = "importing-admin" };

    private ImportAlliancesCommandHandler Handler() => new(_repo, _adminContext);

    [Fact]
    public async Task Handle_NewAlliance_InsertsAndReturnsInsertedCount()
    {
        _repo.FindByCodeAsync("SA", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var dto = new AllianceImportDto("SA", "Star Alliance");

        var result = await handler.Handle(new ImportAlliancesCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.Received(1).AddAsync(Arg.Any<Alliance>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingAlliance_UpdatesAndReturnsUpdatedCount()
    {
        var existing = Alliance.Create("SA", "Old Name");
        _repo.FindByCodeAsync("SA", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = Handler();
        var dto = new AllianceImportDto("SA", "Star Alliance");

        var result = await handler.Handle(new ImportAlliancesCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Empty(result.Errors);
        await _repo.DidNotReceive().AddAsync(Arg.Any<Alliance>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal("Star Alliance", existing.Name);
    }

    // ── #362: import verifies rows ───────────────────────────────────────────

    [Fact]
    public async Task Handle_NewAlliance_NoVerificationValues_EndsVerifiedAsImportByActor()
    {
        _repo.FindByCodeAsync("SA", Arg.Any<CancellationToken>()).ReturnsNull();
        Alliance? added = null;
        await _repo.AddAsync(Arg.Do<Alliance>(a => added = a), Arg.Any<CancellationToken>());
        var handler = Handler();
        var dto = new AllianceImportDto("SA", "Star Alliance");

        await handler.Handle(new ImportAlliancesCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.True(added!.IsVerified);
        Assert.Equal("importing-admin", added.VerifiedBy);
        Assert.Equal(VerificationSources.Import, added.VerificationSource);
    }

    [Fact]
    public async Task Handle_NewAlliance_AllVerificationValuesPresent_KeepsThemAsIs()
    {
        _repo.FindByCodeAsync("SA", Arg.Any<CancellationToken>()).ReturnsNull();
        Alliance? added = null;
        await _repo.AddAsync(Arg.Do<Alliance>(a => added = a), Arg.Any<CancellationToken>());
        var handler = Handler();
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var dto = new AllianceImportDto("SA", "Star Alliance", verifiedAt, "prior-admin", "manual");

        await handler.Handle(new ImportAlliancesCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.True(added!.IsVerified);
        Assert.Equal(verifiedAt, added.VerifiedAt);
        Assert.Equal("prior-admin", added.VerifiedBy);
        Assert.Equal("manual", added.VerificationSource);
    }

    [Fact]
    public async Task Handle_PartiallyFilledVerificationRow_IsRejected_WhileOtherRowInSameFileImports()
    {
        _repo.FindByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var partial = new AllianceImportDto("SA", "Star Alliance", VerifiedBy: "prior-admin"); // VerifiedAt/VerificationSource missing
        var valid = new AllianceImportDto("OW", "Oneworld");

        var result = await handler.Handle(new ImportAlliancesCommand([partial, valid]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Single(result.Errors);
        Assert.Contains("SA", result.Errors[0]);
        await _repo.DidNotReceive().AddAsync(
            Arg.Is<Alliance>(a => a.Code == "SA"), Arg.Any<CancellationToken>());
        await _repo.Received(1).AddAsync(
            Arg.Is<Alliance>(a => a.Code == "OW"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingAllianceInNeedsAttention_IsVerifiedAndIssueCleared()
    {
        var existing = Alliance.Create("SA", "Old Name");
        existing.MarkVerificationFailed("stale seed data");
        _repo.FindByCodeAsync("SA", Arg.Any<CancellationToken>()).Returns(existing);
        var handler = Handler();
        var dto = new AllianceImportDto("SA", "Star Alliance");

        await handler.Handle(new ImportAlliancesCommand([dto]), CancellationToken.None);

        Assert.True(existing.IsVerified);
        Assert.Null(existing.VerificationIssue);
        Assert.Equal(VerificationSources.Import, existing.VerificationSource);
    }
}
