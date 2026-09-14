using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportLoyaltyProgramsCommandHandlerTests
{
    private readonly ILoyaltyRepository _loyaltyRepo = Substitute.For<ILoyaltyRepository>();
    private readonly IRefDataRepository _refRepo = Substitute.For<IRefDataRepository>();
    private readonly IAdminContext _adminContext = new AdminContext { ActorId = "importing-admin" };

    private ImportLoyaltyProgramsCommandHandler Handler() => new(_loyaltyRepo, _refRepo, _adminContext);

    [Fact]
    public async Task Handle_NewProgram_ResolvesKindAndAirlineThenInserts()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        _refRepo.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(5);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync("BAEC", Arg.Any<CancellationToken>()).ReturnsNull();

        var handler = Handler();
        var dto = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", "BA", null, []);

        var result = await handler.Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Empty(result.Errors);
        await _loyaltyRepo.Received(1).AddProgramAsync(Arg.Any<LoyaltyProgram>(), Arg.Any<CancellationToken>());
        await _loyaltyRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnknownKindCode_AddsErrorAndSkipsRecord()
    {
        _loyaltyRepo.FindKindByCodeAsync("unknown", Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = Handler();
        var dto = new LoyaltyProgramImportDto("BAEC", "BA Executive Club", "unknown", null, null, []);

        var result = await handler.Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Single(result.Errors);
        Assert.Contains("unknown", result.Errors[0]);
    }

    [Fact]
    public async Task Handle_ExistingProgram_ReplacesTiersAndUpdates()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        _refRepo.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(5);
        var existing = LoyaltyProgram.Create("BAEC", "Old Name", kind.Id);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync("BAEC", Arg.Any<CancellationToken>()).Returns(existing);

        var handler = Handler();
        var dto = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", "BA", null,
            [new LoyaltyTierImportDto("BLUE", "Blue", 1, null, null)]);

        var result = await handler.Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);
        await _loyaltyRepo.DidNotReceive().AddProgramAsync(Arg.Any<LoyaltyProgram>(), Arg.Any<CancellationToken>());
        await _loyaltyRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── #362: import verifies rows ───────────────────────────────────────────

    [Fact]
    public async Task Handle_NewProgram_NoVerificationValues_EndsVerifiedAsImportByActor()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync("BAEC", Arg.Any<CancellationToken>()).ReturnsNull();
        LoyaltyProgram? added = null;
        await _loyaltyRepo.AddProgramAsync(Arg.Do<LoyaltyProgram>(p => added = p), Arg.Any<CancellationToken>());

        var handler = Handler();
        var dto = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", null, null, []);

        await handler.Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.True(added!.IsVerified);
        Assert.Equal("importing-admin", added.VerifiedBy);
        Assert.Equal(VerificationSources.Import, added.VerificationSource);
    }

    [Fact]
    public async Task Handle_NewProgram_AllVerificationValuesPresent_KeepsThemAsIs()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync("BAEC", Arg.Any<CancellationToken>()).ReturnsNull();
        LoyaltyProgram? added = null;
        await _loyaltyRepo.AddProgramAsync(Arg.Do<LoyaltyProgram>(p => added = p), Arg.Any<CancellationToken>());

        var handler = Handler();
        var verifiedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var dto = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", null, null, [],
            verifiedAt, "prior-admin", "manual");

        await handler.Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.NotNull(added);
        Assert.Equal(verifiedAt, added!.VerifiedAt);
        Assert.Equal("prior-admin", added.VerifiedBy);
        Assert.Equal("manual", added.VerificationSource);
    }

    [Fact]
    public async Task Handle_PartiallyFilledVerificationRow_IsRejected_WhileOtherRowInSameFileImports()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();

        var handler = Handler();
        var partial = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", null, null, [],
            VerifiedBy: "prior-admin"); // VerifiedAt/VerificationSource missing
        var valid = new LoyaltyProgramImportDto("FFMM", "Miles & More", "airline", null, null, []);

        var result = await handler.Handle(new ImportLoyaltyProgramsCommand([partial, valid]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Single(result.Errors);
        Assert.Contains("BAEC", result.Errors[0]);
        await _loyaltyRepo.DidNotReceive().AddProgramAsync(
            Arg.Is<LoyaltyProgram>(p => p.Code == "BAEC"), Arg.Any<CancellationToken>());
        await _loyaltyRepo.Received(1).AddProgramAsync(
            Arg.Is<LoyaltyProgram>(p => p.Code == "FFMM"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExistingProgramInNeedsAttention_IsVerifiedAndIssueCleared()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        var existing = LoyaltyProgram.Create("BAEC", "Old Name", kind.Id);
        existing.MarkVerificationFailed("stale seed data");
        _loyaltyRepo.FindProgramByCodeWithTiersAsync("BAEC", Arg.Any<CancellationToken>()).Returns(existing);

        var handler = Handler();
        var dto = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", null, null, []);

        await handler.Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.True(existing.IsVerified);
        Assert.Null(existing.VerificationIssue);
        Assert.Equal(VerificationSources.Import, existing.VerificationSource);
    }
}
