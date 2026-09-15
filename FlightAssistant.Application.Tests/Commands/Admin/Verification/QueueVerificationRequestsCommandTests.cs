using FlightAssistant.Application.Commands.Admin.Verification;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Tests.Verification;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Verification.Enums;
using NSubstitute;
using static FlightAssistant.Application.Tests.Commands.Admin.Verification.TestSources;

namespace FlightAssistant.Application.Tests.Commands.Admin.Verification;

/// <summary>
/// <see cref="QueueVerificationRequestsCommand"/> (#378): the validator's 400 rules, and what the
/// handler hands the queue.
/// </summary>
public class QueueVerificationRequestsCommandTests
{
    private const string Actor = "admin-oid-1";

    private readonly IVerificationQueue _queue = Substitute.For<IVerificationQueue>();

    private static readonly FakeVerificationSourceRegistry Registry = new(
        Source("ourairports", 0m, VerificationEntityType.Airport),
        Source("aeroapi", 0.015m, VerificationEntityType.Airport, VerificationEntityType.Airline),
        Source("priced", 0.0025m, VerificationEntityType.Airport),
        Source("mledoze", 0m, VerificationEntityType.Country));

    private static QueueVerificationRequestsCommand Command(
        string? entityType = "Airport", IReadOnlyList<string>? keys = null, IReadOnlyList<string>? sources = null, string requestedBy = Actor) =>
        new(entityType, keys ?? new[] { "42" }, sources ?? new[] { "ourairports" }, requestedBy);

    private static FluentValidation.Results.ValidationResult Validate(QueueVerificationRequestsCommand command) =>
        new QueueVerificationRequestsCommandValidator(Registry).Validate(command);

    private static void AssertSingleFailure(QueueVerificationRequestsCommand command, string propertyName, string? messageContains = null)
    {
        var failure = Assert.Single(Validate(command).Errors);
        Assert.Equal(propertyName, failure.PropertyName);
        if (messageContains is not null)
            Assert.Contains(messageContains, failure.ErrorMessage);
    }

    // ── Validator ───────────────────────────────────────────────────────────

    [Fact]
    public void Validator_AValidCommand_Passes()
    {
        var result = Validate(Command(keys: new[] { "1", "42" }, sources: new[] { "ourairports", "aeroapi" }));

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("airport")]
    [InlineData("1")]
    public void Validator_AnInvalidEntityType_Fails(string? entityType)
    {
        AssertSingleFailure(Command(entityType: entityType), "EntityType");
    }

    [Fact]
    public void Validator_EmptyKeys_Fails()
    {
        AssertSingleFailure(Command(keys: Array.Empty<string>()), "Keys");
    }

    [Fact]
    public void Validator_NullKeys_Fails()
    {
        AssertSingleFailure(new QueueVerificationRequestsCommand("Airport", null, new[] { "ourairports" }, Actor), "Keys");
    }

    [Fact]
    public void Validator_EmptySources_Fails()
    {
        AssertSingleFailure(Command(sources: Array.Empty<string>()), "Sources");
    }

    [Fact]
    public void Validator_AnUnknownSource_Fails()
    {
        AssertSingleFailure(Command(sources: new[] { "ourairports", "nope" }), "Sources", "'nope' is not registered");
    }

    [Fact]
    public void Validator_ASourceThatDoesNotSupportTheType_Fails()
    {
        AssertSingleFailure(Command(sources: new[] { "mledoze" }), "Sources", "'mledoze' does not support Airport");
    }

    [Fact]
    public void Validator_AnyAirlineSourceFails_WhileOnlyAirportAndCountrySourcesAreRegistered()
    {
        var registry = new FakeVerificationSourceRegistry(
            Source("ourairports", 0m, VerificationEntityType.Airport),
            Source("mledoze", 0m, VerificationEntityType.Country));

        var result = new QueueVerificationRequestsCommandValidator(registry)
            .Validate(new QueueVerificationRequestsCommand("Airline", new[] { "1" }, new[] { "ourairports" }, Actor));

        Assert.Equal("Sources", Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void Validator_ASourceNameTheDomainRejects_Fails()
    {
        AssertSingleFailure(Command(sources: new[] { "our airports" }), "Sources", "cannot contain whitespace");
    }

    [Theory]
    [InlineData("Airport", "042")]
    [InlineData("Airport", "0")]
    [InlineData("Airport", "-1")]
    [InlineData("Airport", " 42")]
    [InlineData("Airport", "LHR")]
    [InlineData("Airline", "2147483648")]
    [InlineData("Country", "GBR")]
    [InlineData("Country", "")]
    public void Validator_AMalformedKey_FailsOnThatKey(string entityType, string badKey)
    {
        var sources = entityType == "Country" ? new[] { "mledoze" } : new[] { "aeroapi" };

        AssertSingleFailure(Command(entityType, new[] { ValidKey(entityType), badKey }, sources), "Keys[1]");
    }

    [Fact]
    public void Validator_ALowerCaseCountryCode_IsAcceptedBecauseTheDomainCanonicalisesIt()
    {
        Assert.True(Validate(Command("Country", new[] { "gb" }, new[] { "mledoze" })).IsValid);
    }

    [Fact]
    public void Validator_ExactlyMaxKeys_Passes()
    {
        var keys = Enumerable.Range(1, QueueVerificationRequestsCommand.MaxKeys).Select(i => i.ToString()).ToList();

        Assert.True(Validate(Command(keys: keys)).IsValid);
    }

    [Fact]
    public void Validator_MoreThanMaxKeys_FailsOnce()
    {
        var keys = Enumerable.Range(1, QueueVerificationRequestsCommand.MaxKeys + 1).Select(i => i.ToString()).ToList();

        AssertSingleFailure(Command(keys: keys), "Keys", "At most 1000");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Validator_ABlankActor_Fails(string requestedBy)
    {
        AssertSingleFailure(Command(requestedBy: requestedBy), "RequestedBy");
    }

    // ── Handler ─────────────────────────────────────────────────────────────

    private QueueVerificationRequestsCommandHandler Handler() => new(_queue, Registry);

    [Fact]
    public async Task Handle_TwoSources_StoresTheSumOfTheirPerRowCost()
    {
        await Handler().Handle(Command(sources: new[] { "aeroapi", "priced" }), CancellationToken.None);

        await _queue.Received(1).EnqueueAsync(
            Arg.Any<VerificationEntityType>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<VerificationTrigger>(), Arg.Any<string>(), 0.0175m, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ASourceNamedTwice_IsPricedAndPassedOnce()
    {
        await Handler().Handle(Command(sources: new[] { "aeroapi", "ourairports", "aeroapi" }), CancellationToken.None);

        await _queue.Received(1).EnqueueAsync(
            Arg.Any<VerificationEntityType>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Is<IReadOnlyCollection<string>>(s => s.SequenceEqual(new[] { "aeroapi", "ourairports" })),
            Arg.Any<VerificationTrigger>(), Arg.Any<string>(), 0.015m, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PassesTheTypeKeysSourcesActorAndTheAdminTrigger()
    {
        await Handler().Handle(
            Command("Country", new[] { "gb", "FR" }, new[] { "mledoze" }, "admin-oid-7"), CancellationToken.None);

        await _queue.Received(1).EnqueueAsync(
            VerificationEntityType.Country,
            Arg.Is<IReadOnlyCollection<string>>(k => k.SequenceEqual(new[] { "gb", "FR" })),
            Arg.Is<IReadOnlyCollection<string>>(s => s.SequenceEqual(new[] { "mledoze" })),
            VerificationTrigger.Admin,
            "admin-oid-7",
            0m,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ReturnsTheQueuesQueuedAndSkippedCounts()
    {
        _queue.EnqueueAsync(
                Arg.Any<VerificationEntityType>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<VerificationTrigger>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(new VerificationEnqueueResult(Queued: 2, Skipped: 3));

        var result = await Handler().Handle(Command(keys: new[] { "1", "2", "3", "4", "5" }), CancellationToken.None);

        Assert.Equal(new VerificationEnqueueResult(2, 3), result);
    }

    [Fact]
    public async Task Handle_AnUnknownSourceOutsideThePipeline_Is400_AndQueuesNothing()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            Handler().Handle(Command(sources: new[] { "nope" }), CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Empty(_queue.ReceivedCalls());
    }

    private static string ValidKey(string entityType) => entityType == "Country" ? "GB" : "1";
}
