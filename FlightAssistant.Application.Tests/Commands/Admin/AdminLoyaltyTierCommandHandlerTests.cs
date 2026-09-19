using FlightAssistant.Application.Commands.Admin.Loyalty;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Loyalty.Entities;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin;

/// <summary>
/// Writing a program's tiers (#488). A tier is always addressed through the program that owns it,
/// so every handler here starts from the program aggregate and writes through it.
/// </summary>
public class AdminLoyaltyTierCommandHandlerTests
{
    private readonly ILoyaltyRepository _repo = Substitute.For<ILoyaltyRepository>();

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_AddsTheTierToTheProgramAndSaves()
    {
        var program = GivenProgram(1);
        var handler = new CreateLoyaltyTierCommandHandler(_repo);

        await handler.Handle(
            new CreateLoyaltyTierCommand(1, "GOLD", "Gold", 3, "STAR", "*G"),
            CancellationToken.None);

        var tier = Assert.Single(program.Tiers);
        Assert.Equal("GOLD", tier.Code);
        Assert.Equal("Gold", tier.Name);
        Assert.Equal(3, tier.Rank);
        Assert.Equal("STAR", tier.AllianceCode);
        Assert.Equal("*G", tier.AllianceTierCode);
        Assert.Equal(1, tier.ProgramId);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_UnknownProgram_Throws404AndSavesNothing()
    {
        _repo.GetProgramByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new CreateLoyaltyTierCommandHandler(_repo);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(new CreateLoyaltyTierCommand(99, "GOLD", "Gold", 3, null, null), CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// loyalty_tiers is unique on (ProgramId, Code). Without this check the insert reaches the
    /// database and its index failure surfaces as a 500 rather than a conflict the panel can show.
    /// </summary>
    [Fact]
    public async Task Create_CodeAlreadyOnThisProgram_Throws409AndSavesNothing()
    {
        var program = GivenProgram(1, ("GOLD", 3, 10));
        var handler = new CreateLoyaltyTierCommandHandler(_repo);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(new CreateLoyaltyTierCommand(1, "GOLD", "Gold reissued", 4, null, null), CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("GOLD", ex.Message);
        Assert.Single(program.Tiers);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>The other unique index on the table: (ProgramId, Rank).</summary>
    [Fact]
    public async Task Create_RankAlreadyOnThisProgram_Throws409AndSavesNothing()
    {
        var program = GivenProgram(1, ("GOLD", 3, 10));
        var handler = new CreateLoyaltyTierCommandHandler(_repo);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(new CreateLoyaltyTierCommand(1, "SILVER", "Silver", 3, null, null), CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("3", ex.Message);
        Assert.Single(program.Tiers);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ExistingTier_UpdatesItAndSaves()
    {
        var program = GivenProgram(1, ("GOLD", 3, 10));
        var handler = new UpdateLoyaltyTierCommandHandler(_repo);

        await handler.Handle(
            new UpdateLoyaltyTierCommand(1, 10, "GOLD", "Gold Elite", 4, "OW", "EMD"),
            CancellationToken.None);

        var tier = Assert.Single(program.Tiers);
        Assert.Equal("Gold Elite", tier.Name);
        Assert.Equal(4, tier.Rank);
        Assert.Equal("OW", tier.AllianceCode);
        Assert.Equal("EMD", tier.AllianceTierCode);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The tier is looked up inside the program, so a tier id belonging to another program is a
    /// 404 here: one program's route must never reach through to another program's tier.
    /// </summary>
    [Fact]
    public async Task Update_TierThatIsNotOnThisProgram_Throws404AndSavesNothing()
    {
        GivenProgram(1, ("GOLD", 3, 10));
        var handler = new UpdateLoyaltyTierCommandHandler(_repo);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(new UpdateLoyaltyTierCommand(1, 77, "GOLD", "Gold", 3, null, null), CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_CodeTakenByAnotherTierOfTheProgram_Throws409AndSavesNothing()
    {
        var program = GivenProgram(1, ("GOLD", 3, 10), ("SILVER", 2, 11));
        var handler = new UpdateLoyaltyTierCommandHandler(_repo);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(new UpdateLoyaltyTierCommand(1, 11, "GOLD", "Silver", 2, null, null), CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("SILVER", program.Tiers.Single(t => t.Id == 11).Code);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_RankTakenByAnotherTierOfTheProgram_Throws409AndSavesNothing()
    {
        var program = GivenProgram(1, ("GOLD", 3, 10), ("SILVER", 2, 11));
        var handler = new UpdateLoyaltyTierCommandHandler(_repo);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(new UpdateLoyaltyTierCommand(1, 11, "SILVER", "Silver", 3, null, null), CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(2, program.Tiers.Single(t => t.Id == 11).Rank);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The conflict check must exclude the tier being edited, or renaming a tier while leaving its
    /// own code and rank alone would report a conflict with itself.
    /// </summary>
    [Fact]
    public async Task Update_KeepingItsOwnCodeAndRank_Saves()
    {
        var program = GivenProgram(1, ("GOLD", 3, 10));
        var handler = new UpdateLoyaltyTierCommandHandler(_repo);

        await handler.Handle(
            new UpdateLoyaltyTierCommand(1, 10, "GOLD", "Gold renamed", 3, null, null),
            CancellationToken.None);

        Assert.Equal("Gold renamed", program.Tiers.Single().Name);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_UnknownProgram_Throws404AndSavesNothing()
    {
        _repo.GetProgramByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new UpdateLoyaltyTierCommandHandler(_repo);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(new UpdateLoyaltyTierCommand(99, 10, "GOLD", "Gold", 3, null, null), CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ExistingTier_RemovesItAndSaves()
    {
        var program = GivenProgram(1, ("GOLD", 3, 10), ("SILVER", 2, 11));
        var handler = new DeleteLoyaltyTierCommandHandler(_repo);

        await handler.Handle(new DeleteLoyaltyTierCommand(1, 10), CancellationToken.None);

        Assert.Equal(["SILVER"], program.Tiers.Select(t => t.Code));
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Unlike deleting a program, which returns silently when there is nothing to delete, an
    /// unknown tier is a 404: the panel addresses a tier through its program, and a tier id that
    /// isn't on that program is a mistake worth reporting rather than a no-op to swallow.
    /// </summary>
    [Fact]
    public async Task Delete_TierThatIsNotOnThisProgram_Throws404AndSavesNothing()
    {
        var program = GivenProgram(1, ("GOLD", 3, 10));
        var handler = new DeleteLoyaltyTierCommandHandler(_repo);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(new DeleteLoyaltyTierCommand(1, 77), CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Single(program.Tiers);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_UnknownProgram_Throws404AndSavesNothing()
    {
        _repo.GetProgramByIdAsync(99, Arg.Any<CancellationToken>()).ReturnsNull();
        var handler = new DeleteLoyaltyTierCommandHandler(_repo);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(new DeleteLoyaltyTierCommand(99, 10), CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A program with the given tiers, wired into the repository under `programId`. Ids are set the
    /// way the rest of the suite sets them — the database assigns them in real life.
    /// </summary>
    private LoyaltyProgram GivenProgram(int programId, params (string Code, int Rank, int Id)[] tiers)
    {
        var program = LoyaltyProgram.Create("BAEC", "BA Executive Club", 1, null);
        SetId(program, programId);
        foreach (var (code, rank, id) in tiers)
        {
            var tier = LoyaltyTier.Create(programId, code, code.ToLowerInvariant(), rank);
            SetId(tier, id);
            program.AddTier(tier);
        }
        _repo.GetProgramByIdAsync(programId, Arg.Any<CancellationToken>()).Returns(program);
        return program;
    }

    private static void SetId<TEntity>(TEntity entity, int id) =>
        typeof(TEntity).GetProperty("Id")!.SetValue(entity, id);
}
