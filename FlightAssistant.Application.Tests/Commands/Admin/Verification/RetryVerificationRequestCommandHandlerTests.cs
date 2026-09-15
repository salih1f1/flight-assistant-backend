using FlightAssistant.Application.Commands.Admin.Verification;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Tests.Verification;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using static FlightAssistant.Application.Tests.Commands.Admin.Verification.TestSources;

namespace FlightAssistant.Application.Tests.Commands.Admin.Verification;

/// <summary><see cref="RetryVerificationRequestCommandHandler"/> (#378, spec §4.4).</summary>
public class RetryVerificationRequestCommandHandlerTests
{
    private static readonly DateTime RequestedAt = new(2026, 9, 14, 8, 0, 0, DateTimeKind.Utc);
    private const long RequestId = 7;
    private const string Actor = "admin-oid-1";

    private readonly IVerificationRequestRepository _requests = Substitute.For<IVerificationRequestRepository>();
    private readonly IVerificationQueue _queue = Substitute.For<IVerificationQueue>();

    private static readonly FakeVerificationSourceRegistry Registry = new(
        Source("ourairports", 0m, VerificationEntityType.Airport),
        Source("aeroapi", 0.015m, VerificationEntityType.Airport, VerificationEntityType.Airline),
        Source("priced", 0.0025m, VerificationEntityType.Airport),
        Source("mledoze", 0m, VerificationEntityType.Country));

    public RetryVerificationRequestCommandHandlerTests()
    {
        QueueAnswers(new VerificationEnqueueResult(Queued: 1, Skipped: 0));
    }

    private Task<VerificationEnqueueResult> Retry(string requestedBy = Actor) =>
        new RetryVerificationRequestCommandHandler(_requests, _queue, Registry)
            .Handle(new RetryVerificationRequestCommand(RequestId, requestedBy), CancellationToken.None);

    private void QueueAnswers(VerificationEnqueueResult result) =>
        _queue.EnqueueAsync(
                Arg.Any<VerificationEntityType>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<VerificationTrigger>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(result);

    /// <summary>A completed request, as a retry usually finds it.</summary>
    private VerificationRequest StoredRequest(VerificationEntityType entityType, string entityKey, params string[] sources)
    {
        var request = VerificationRequest.Create(
            entityType, entityKey, sources, VerificationTrigger.Flight, "system", 0m, RequestedAt);
        request.MarkError("ourairports: Timed out downloading the ourairports dataset", RequestedAt.AddMinutes(5));
        _requests.GetByIdAsync(RequestId, Arg.Any<CancellationToken>()).Returns(request);
        return request;
    }

    [Fact]
    public async Task Handle_TheRequestDoesNotExist_Is404_AndQueuesNothing()
    {
        _requests.GetByIdAsync(RequestId, Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await Assert.ThrowsAsync<DomainException>(() => Retry());

        Assert.Equal(404, ex.StatusCode);
        Assert.Empty(_queue.ReceivedCalls());
    }

    [Fact]
    public async Task Handle_ASourceIsNoLongerRegistered_Is400_AndQueuesNothing()
    {
        StoredRequest(VerificationEntityType.Airport, "42", "ourairports", "retired");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Retry());

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("'retired' is not registered", ex.Message);
        Assert.Empty(_queue.ReceivedCalls());
    }

    [Fact]
    public async Task Handle_ASourceDoesNotSupportTheEntityType_Is400_AndQueuesNothing()
    {
        StoredRequest(VerificationEntityType.Country, "GB", "mledoze", "ourairports");

        var ex = await Assert.ThrowsAsync<DomainException>(() => Retry());

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("'ourairports' does not support Country", ex.Message);
        Assert.Empty(_queue.ReceivedCalls());
    }

    [Fact]
    public async Task Handle_TheEntityAlreadyHasAnOpenRequest_Is409()
    {
        StoredRequest(VerificationEntityType.Airport, "42", "ourairports");
        QueueAnswers(new VerificationEnqueueResult(Queued: 0, Skipped: 1));

        var ex = await Assert.ThrowsAsync<DomainException>(() => Retry());

        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("Airport 42 already has an open verification request", ex.Message);
    }

    [Fact]
    public async Task Handle_QueuesTheSameEntityAndSources_AsAnAdminRetry_WithTheActorAndTheRegistrysCost()
    {
        StoredRequest(VerificationEntityType.Airport, "42", "aeroapi", "priced");

        var result = await Retry(requestedBy: "admin-oid-9");

        Assert.Equal(new VerificationEnqueueResult(1, 0), result);
        await _queue.Received(1).EnqueueAsync(
            VerificationEntityType.Airport,
            Arg.Is<IReadOnlyCollection<string>>(keys => keys.SequenceEqual(new[] { "42" })),
            Arg.Is<IReadOnlyCollection<string>>(sources => sources.SequenceEqual(new[] { "aeroapi", "priced" })),
            VerificationTrigger.Admin,
            "admin-oid-9",
            0.0175m,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ACountryRequest_IsRetriedByItsCode()
    {
        StoredRequest(VerificationEntityType.Country, "gb", "mledoze");

        await Retry();

        await _queue.Received(1).EnqueueAsync(
            VerificationEntityType.Country,
            Arg.Is<IReadOnlyCollection<string>>(keys => keys.SequenceEqual(new[] { "GB" })),
            Arg.Is<IReadOnlyCollection<string>>(sources => sources.SequenceEqual(new[] { "mledoze" })),
            VerificationTrigger.Admin,
            Actor,
            0m,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0L, Actor, "RequestId")]
    [InlineData(RequestId, "", "RequestedBy")]
    public void Validator_RejectsAMissingIdOrActor(long requestId, string requestedBy, string property)
    {
        var result = new RetryVerificationRequestCommandValidator()
            .Validate(new RetryVerificationRequestCommand(requestId, requestedBy));

        Assert.Equal(property, Assert.Single(result.Errors).PropertyName);
    }
}
