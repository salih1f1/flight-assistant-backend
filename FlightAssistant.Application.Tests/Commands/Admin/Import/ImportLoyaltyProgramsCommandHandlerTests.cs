using System.Text.Json;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Interfaces.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Import;

public class ImportLoyaltyProgramsCommandHandlerTests
{
    private readonly ILoyaltyRepository _loyaltyRepo = Substitute.For<ILoyaltyRepository>();
    private readonly IRefDataRepository _refRepo = Substitute.For<IRefDataRepository>();
    private readonly IAllianceRepository _alliances = Substitute.For<IAllianceRepository>();
    private readonly IAdminContext _adminContext = new AdminContext { ActorId = "importing-admin" };

    public ImportLoyaltyProgramsCommandHandlerTests() =>
        // Alliance codes resolve unless a test says otherwise — see the same note on
        // AdminLoyaltyTierCommandHandlerTests.
        _alliances.FindByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Alliance.Create(ci.Arg<string>(), "Some Alliance"));

    private ImportLoyaltyProgramsCommandHandler Handler() => new(_loyaltyRepo, _refRepo, _alliances, _adminContext);

    [Fact]
    public async Task Handle_NewProgram_ResolvesKindAndAirlineThenInserts()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        _refRepo.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(5);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync("BAEC", Arg.Any<CancellationToken>()).ReturnsNull();

        var handler = Handler();
        var dto = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", "BA", []);

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
        var dto = new LoyaltyProgramImportDto("BAEC", "BA Executive Club", "unknown", null, []);

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
        var dto = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", "BA",
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
        var dto = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", null, []);

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
        var dto = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", null, [],
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
        var partial = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", null, [],
            VerifiedBy: "prior-admin"); // VerifiedAt/VerificationSource missing
        var valid = new LoyaltyProgramImportDto("FFMM", "Miles & More", "airline", null, []);

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
        var dto = new LoyaltyProgramImportDto("BAEC", "British Airways Executive Club", "airline", null, []);

        await handler.Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.True(existing.IsVerified);
        Assert.Null(existing.VerificationIssue);
        Assert.Equal(VerificationSources.Import, existing.VerificationSource);
    }

    /// <summary>
    /// #504: a program-level allianceCode is no longer part of the import contract. A file written
    /// before the change still imports — System.Text.Json ignores the unmapped property — and the
    /// program is created without an alliance, because the alliance belongs to its airline.
    ///
    /// <para>The JSON-still-binds assertions alone would stay green even if <c>AllianceCode</c> were
    /// restored to <see cref="LoyaltyProgramImportDto"/>, so this also asserts the property is gone
    /// from the DTO's shape, and actually runs the handler on the deserialized item to prove the
    /// import pipeline accepts the older file end to end.</para>
    /// </summary>
    [Fact]
    public async Task Handle_IgnoresAProgramLevelAllianceCodeFromAnOlderFile()
    {
        var json = """
        [
          { "code": "BA-EXEC", "name": "British Airways Executive Club", "kindCode": "FF",
            "airlineIata": "BA", "allianceCode": "OW", "tiers": [] }
        ]
        """;

        var items = JsonSerializer.Deserialize<List<LoyaltyProgramImportDto>>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Single(items);
        Assert.Equal("BA-EXEC", items[0].Code);
        Assert.Equal("BA", items[0].AirlineIata);
        // Fails if AllianceCode were restored to the DTO — the assertions above would not catch that.
        Assert.Empty(typeof(LoyaltyProgramImportDto).GetProperties().Where(p => p.Name == "AllianceCode"));

        var kind = LoyaltyProgramKind.Create("FF", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("FF", Arg.Any<CancellationToken>()).Returns(kind);
        _refRepo.GetAirlineIdByIataAsync("BA", Arg.Any<CancellationToken>()).Returns(5);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync("BA-EXEC", Arg.Any<CancellationToken>()).ReturnsNull();
        LoyaltyProgram? added = null;
        await _loyaltyRepo.AddProgramAsync(Arg.Do<LoyaltyProgram>(p => added = p), Arg.Any<CancellationToken>());

        var handler = Handler();
        var result = await handler.Handle(new ImportLoyaltyProgramsCommand(items), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Empty(result.Errors);
        Assert.NotNull(added);
        Assert.Equal("BA-EXEC", added!.Code);
    }

    // ── The alliance-code guard (#508) ────────────────────────────────────────

    /// <summary>
    /// The guard sits inside the per-row try, like the two airline/airport importers': this handler
    /// saves once after the loop, so without it one bad alliance code in a file of 500 programs
    /// would fail every row with a 500 while Errors came back empty.
    /// </summary>
    [Fact]
    public async Task Handle_TierNamesAnUnknownAlliance_IsThatRowsErrorAndTheRestStillImports()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        _alliances.FindByCodeAsync("STAR", Arg.Any<CancellationToken>()).ReturnsNull();

        var bad = new LoyaltyProgramImportDto("MS", "Miles and Smiles", "airline", null,
            [new LoyaltyTierImportDto("ELITE", "Elite", 1, "STAR", "*G")]);
        var good = new LoyaltyProgramImportDto("BAEC", "Executive Club", "airline", null,
            [new LoyaltyTierImportDto("SILVER", "Silver", 1, "OW", "SAPPHIRE")]);

        var result = await Handler().Handle(new ImportLoyaltyProgramsCommand([bad, good]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        var error = Assert.Single(result.Errors);
        Assert.Contains("MS", error);
        Assert.Contains("STAR", error);
        await _loyaltyRepo.Received(1).AddProgramAsync(
            Arg.Is<LoyaltyProgram>(p => p.Code == "BAEC"), Arg.Any<CancellationToken>());
        await _loyaltyRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// And it sits before the first mutation, not merely inside the try. The update branch edits a
    /// tracked LoyaltyProgram before it rebuilds the tiers, and the one SaveChangesAsync after the
    /// loop would otherwise commit that edit for a row whose tier was refused — a half-applied row,
    /// reported as an error.
    /// </summary>
    [Fact]
    public async Task Handle_ExistingProgramWhoseTierNamesAnUnknownAlliance_IsNotPartlyUpdated()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        var existing = LoyaltyProgram.Create("MS", "Miles and Smiles", kind.Id, null);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync("MS", Arg.Any<CancellationToken>()).Returns(existing);
        _alliances.FindByCodeAsync("STAR", Arg.Any<CancellationToken>()).ReturnsNull();

        var dto = new LoyaltyProgramImportDto("MS", "Renamed By The Import", "airline", null,
            [new LoyaltyTierImportDto("ELITE", "Elite", 1, "STAR", "*G")]);

        var result = await Handler().Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.Equal(0, result.Updated);
        Assert.Single(result.Errors);
        Assert.Equal("Miles and Smiles", existing.Name);
        Assert.Empty(existing.Tiers);
    }

    /// <summary>
    /// A tier belonging to no alliance is the common case and must cost no lookup.
    /// </summary>
    [Fact]
    public async Task Handle_TiersWithNoAllianceCode_LookNothingUp()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();

        var dto = new LoyaltyProgramImportDto("BAEC", "Executive Club", "airline", null,
            [new LoyaltyTierImportDto("SILVER", "Silver", 1, null, null),
             new LoyaltyTierImportDto("BRONZE", "Bronze", 2, "  ", null)]);

        var result = await Handler().Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Empty(result.Errors);
        await _alliances.DidNotReceive().FindByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// One lookup per distinct code, not one per tier: a programme's tiers normally all belong to
    /// the same alliance, and a 20-tier file should not issue 20 identical queries.
    /// </summary>
    [Fact]
    public async Task Handle_ManyTiersOfOneAlliance_LooksTheCodeUpOnce()
    {
        var kind = LoyaltyProgramKind.Create("airline", "Airline");
        _loyaltyRepo.FindKindByCodeAsync("airline", Arg.Any<CancellationToken>()).Returns(kind);
        _loyaltyRepo.FindProgramByCodeWithTiersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();

        var dto = new LoyaltyProgramImportDto("BAEC", "Executive Club", "airline", null,
            [new LoyaltyTierImportDto("SILVER", "Silver", 1, "OW", "SAPPHIRE"),
             new LoyaltyTierImportDto("GOLD", "Gold", 2, "ow", "EMERALD"),
             new LoyaltyTierImportDto("BRONZE", "Bronze", 3, "OW", "RUBY")]);

        var result = await Handler().Handle(new ImportLoyaltyProgramsCommand([dto]), CancellationToken.None);

        Assert.Empty(result.Errors);
        await _alliances.Received(1).FindByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
