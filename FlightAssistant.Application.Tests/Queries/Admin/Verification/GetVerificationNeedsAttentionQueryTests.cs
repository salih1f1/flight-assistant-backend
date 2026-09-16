using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Verification;
using FlightAssistant.Domain.Verification.Enums;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Verification;

/// <summary>
/// <see cref="GetVerificationNeedsAttentionQuery"/> (#385, Task 2): the handler is a pure pass-through
/// of <see cref="IVerificationNeedsAttentionRepository.GetCountsAsync"/> -- the all-three/enum-order/
/// zero-fill guarantee (controller ruling R3) belongs to the repository, so what this test pins is that
/// the handler adds nothing and drops nothing.
/// </summary>
public class GetVerificationNeedsAttentionQueryTests
{
    private readonly IVerificationNeedsAttentionRepository _repository = Substitute.For<IVerificationNeedsAttentionRepository>();

    private Task<IReadOnlyList<VerificationNeedsAttentionCount>> Handle() =>
        new GetVerificationNeedsAttentionQueryHandler(_repository)
            .Handle(new GetVerificationNeedsAttentionQuery(), CancellationToken.None);

    [Fact]
    public async Task Handle_ReturnsTheRepositorysCountsUnchanged_InTheirGivenOrder_IncludingZeros()
    {
        var counts = new List<VerificationNeedsAttentionCount>
        {
            new(VerificationEntityType.Airport, 3),
            new(VerificationEntityType.Airline, 0),
            new(VerificationEntityType.Country, 7),
        };
        _repository.GetCountsAsync(Arg.Any<CancellationToken>()).Returns(counts);

        var results = await Handle();

        Assert.Equal(counts, results);
    }

    [Fact]
    public async Task Handle_PassesTheCancellationTokenThrough()
    {
        using var cts = new CancellationTokenSource();
        _repository.GetCountsAsync(Arg.Any<CancellationToken>()).Returns(new List<VerificationNeedsAttentionCount>());

        await new GetVerificationNeedsAttentionQueryHandler(_repository)
            .Handle(new GetVerificationNeedsAttentionQuery(), cts.Token);

        await _repository.Received(1).GetCountsAsync(cts.Token);
    }
}
