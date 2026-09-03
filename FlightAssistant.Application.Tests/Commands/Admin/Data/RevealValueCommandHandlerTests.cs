using System.Text.Json;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Data;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Administration.Entities;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Travelers.Entities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Data;

/// <summary>
/// <see cref="RevealValueCommandHandler"/> (#318). The property under test throughout this file is
/// the ordering the issue exists for: a value is only ever handed back once its audit row has
/// actually committed. <see cref="Handle_WhenTheAuditSaveFails_PropagatesAndNeverReturnsAValue"/>
/// is the one test that fails if that ordering is ever reversed — the comment in the handler is
/// not, by itself, load-bearing.
///
/// <para>
/// Column-sensitivity validation (a non-sensitive or unknown column -&gt; 400) lives entirely in
/// <see cref="RevealValueCommandValidator"/>, not in the handler. Per CLAUDE.md, validators in
/// this codebase are not tested in isolation — they're exercised through the MediatR pipeline —
/// so those cases are covered in <c>AdminDataEndpointTests</c> instead of here.
/// </para>
/// </summary>
public class RevealValueCommandHandlerTests
{
    private readonly IBrowsableDataRepository _dataRepository = Substitute.For<IBrowsableDataRepository>();
    private readonly IAdminAuditRepository _auditRepository = Substitute.For<IAdminAuditRepository>();
    private readonly AdminContext _adminContext = new() { ActorId = "actor-oid-1", ActorName = "Ada Admin" };

    private RevealValueCommandHandler Handler() => new(_dataRepository, _auditRepository, _adminContext);

    // ── Unknown table / id: 404, and never an audit row ─────────────────────────

    /// <summary>
    /// The handler's own defensive re-check (mirroring <c>GetBrowsableDataQueryHandler</c>) —
    /// exercised directly here since a caller other than <c>AdminDataEndpoints</c> could reach the
    /// handler without the endpoint's own slug check ever running.
    /// </summary>
    [Fact]
    public async Task Handle_UnknownTable_Throws404_AndNeverTouchesEitherRepository()
    {
        var command = new RevealValueCommand("not-a-real-table", Guid.NewGuid().ToString(), "Email");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Handler().Handle(command, CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        await _dataRepository.DidNotReceive().GetRowAsync(
            Arg.Any<BrowsableTable>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _auditRepository.DidNotReceive().AddAsync(Arg.Any<AdminAuditLog>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The ordering rule most likely to be gotten wrong (#318's own warning): the row lookup must
    /// happen, and fail, before anything about an audit row is ever built. Nothing was revealed, so
    /// nothing is recorded.
    /// </summary>
    [Fact]
    public async Task Handle_UnknownId_Throws404_AndWritesNoAuditRow()
    {
        _dataRepository
            .GetRowAsync(Arg.Any<BrowsableTable>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        var command = new RevealValueCommand("travelers", Guid.NewGuid().ToString(), nameof(Traveler.Email));

        var ex = await Assert.ThrowsAsync<DomainException>(() => Handler().Handle(command, CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        await _auditRepository.DidNotReceive().AddAsync(Arg.Any<AdminAuditLog>(), Arg.Any<CancellationToken>());
        await _auditRepository.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── The ordering is the feature ─────────────────────────────────────────────

    /// <summary>
    /// THE security-critical test (#318): forces the audit save to fail and asserts the caller
    /// never receives a value. If <see cref="RevealValueCommandHandler.Handle"/> ever returned the
    /// value before <c>SaveChangesAsync</c> completed — or awaited it but swallowed the exception —
    /// this is the test that would catch it. Without it, the ordering in the handler is only a
    /// comment.
    /// </summary>
    [Fact]
    public async Task Handle_WhenTheAuditSaveFails_PropagatesAndNeverReturnsAValue()
    {
        var row = new Dictionary<string, object?> { [nameof(Traveler.Email)] = "secret@example.com" };
        _dataRepository
            .GetRowAsync(Arg.Any<BrowsableTable>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(row);
        _auditRepository.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated database failure"));

        var command = new RevealValueCommand("travelers", Guid.NewGuid().ToString(), nameof(Traveler.Email));

        // Propagates as-is — an unhandled InvalidOperationException, never wrapped into a
        // DomainException — which is exactly what GlobalExceptionHandler's catch-all maps to a
        // 500. Asserting the throw is what proves Handle never reached its `return`.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler().Handle(command, CancellationToken.None));
    }

    // ── Successful reveal ────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_SuccessfulReveal_WritesTheAuditRowAndReturnsTheValue()
    {
        var row = new Dictionary<string, object?> { [nameof(Traveler.Email)] = "secret@example.com" };
        _dataRepository
            .GetRowAsync(Arg.Any<BrowsableTable>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(row);

        var id = Guid.NewGuid().ToString();
        var command = new RevealValueCommand("travelers", id, nameof(Traveler.Email));

        var result = await Handler().Handle(command, CancellationToken.None);

        Assert.Equal("travelers", result.Table);
        Assert.Equal(id, result.Id);
        Assert.Equal(nameof(Traveler.Email), result.Column);
        Assert.Equal("secret@example.com", result.Value);

        await _auditRepository.Received(1).AddAsync(Arg.Any<AdminAuditLog>(), Arg.Any<CancellationToken>());
        await _auditRepository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The other security-critical property (#318): the written row records *that* a reveal
    /// happened, never the value itself. Asserted against the whole serialized entry — not a
    /// field-by-field spot check, which could miss a future change that stuffed the value into some
    /// property this test didn't think to look at — and paired with a control (the column name,
    /// which <c>Summary</c> deliberately does name) present in that same serialized text, so a
    /// passing assertion is proven to discriminate rather than passing regardless of whether the
    /// value ever made it in. Per the brief: "five tests in this project have shipped that passed
    /// either way."
    /// </summary>
    [Fact]
    public async Task Handle_SuccessfulReveal_AuditRowNeverContainsTheRevealedValue_ButNamesTheColumnAsAControl()
    {
        const string secretValue = "very-secret-value-2a91f7";
        var row = new Dictionary<string, object?> { [nameof(Traveler.Email)] = secretValue };
        _dataRepository
            .GetRowAsync(Arg.Any<BrowsableTable>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(row);

        AdminAuditLog? captured = null;
        _auditRepository
            .AddAsync(Arg.Do<AdminAuditLog>(a => captured = a), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var entityId = Guid.NewGuid().ToString();
        var command = new RevealValueCommand("travelers", entityId, nameof(Traveler.Email));

        await Handler().Handle(command, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(AdminAuditAction.Reveal, captured!.Action);
        Assert.Equal("actor-oid-1", captured.ActorId);
        Assert.Equal("Ada Admin", captured.ActorName);
        Assert.Equal(nameof(Traveler), captured.EntityType);
        Assert.Equal(entityId, captured.EntityId);
        Assert.Null(captured.Before);
        Assert.Null(captured.After);

        var serialized = JsonSerializer.Serialize(captured);
        Assert.Contains(nameof(Traveler.Email), serialized); // control: proves this check sees real content
        Assert.DoesNotContain(secretValue, serialized);
    }

    [Fact]
    public async Task Handle_NoAuthenticatedAdminActor_Throws_AndWritesNoAuditRow()
    {
        _adminContext.ActorId = null;
        var row = new Dictionary<string, object?> { [nameof(Traveler.Email)] = "secret@example.com" };
        _dataRepository
            .GetRowAsync(Arg.Any<BrowsableTable>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(row);

        var command = new RevealValueCommand("travelers", Guid.NewGuid().ToString(), nameof(Traveler.Email));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler().Handle(command, CancellationToken.None));
        await _auditRepository.DidNotReceive().AddAsync(Arg.Any<AdminAuditLog>(), Arg.Any<CancellationToken>());
    }
}
