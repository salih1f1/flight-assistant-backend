using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Data;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Domain.Flights.Enums;
using FlightAssistant.Domain.Travelers.Entities;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Data;

/// <summary>
/// <see cref="GetBrowsableDataQueryHandler"/> (#392 final review). The first handler-level test for
/// this query — everything else about it (auth, unknown table/column, masking, enum rendering) is
/// already covered end-to-end via <c>AdminDataEndpointTests</c>, which is sufficient for every case
/// except one: whether masking or enum-rendering runs first inside <c>Project</c>'s ternary. No
/// enum-typed column is registered in <c>SensitiveProperties</c> today (see the plan's own recorded
/// gap), so that ordering is otherwise unobservable through the real repository and database — no
/// row the API can ever actually return exercises both branches at once.
///
/// <para>
/// Stubbing <see cref="IBrowsableDataRepository"/> closes the gap honestly rather than by inventing
/// a fake registry entry: the row <see cref="IBrowsableDataRepository.QueryAsync"/> returns is an
/// ordinary <c>IReadOnlyDictionary&lt;string, object?&gt;</c>, and nothing about that contract stops
/// a value from being an enum — <c>Traveler.Email</c> is already, genuinely, registered sensitive
/// in <c>SensitiveProperties</c>; only the value under it is synthetic.
/// </para>
/// </summary>
public class GetBrowsableDataQueryHandlerTests
{
    private readonly IBrowsableDataRepository _repository = Substitute.For<IBrowsableDataRepository>();
    private readonly GetBrowsableDataQueryHandler _handler;

    public GetBrowsableDataQueryHandlerTests() => _handler = new GetBrowsableDataQueryHandler(_repository);

    /// <summary>
    /// Proves masking wins even when the sensitive column happens to hold an enum value: the
    /// projection must return <see cref="SensitiveProperties.RedactedPlaceholder"/>, never the
    /// enum's name (<c>"Fast"</c>) and never its underlying number. Swapping <c>Project</c>'s
    /// ternary branches — enum check before the sensitivity check — is the one change that flips
    /// this test's outcome; nothing else in the suite would notice.
    /// </summary>
    [Fact]
    public async Task Handle_ASensitiveColumnHoldingAnEnum_IsRedacted_NotRenderedAsItsName()
    {
        IReadOnlyDictionary<string, object?> row = new Dictionary<string, object?>
        {
            [nameof(Traveler.Email)] = FlightSyncMode.Fast
        };
        _repository.QueryAsync(
                Arg.Any<BrowsableTable>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<IReadOnlyList<(string Column, string Value)>>(),
                Arg.Any<CancellationToken>())
            .Returns((new[] { row }, 1));

        var query = new GetBrowsableDataQuery("travelers", new PagedRequest());

        var result = await _handler.Handle(query, CancellationToken.None);

        var resultRow = Assert.Single(result.Items);
        Assert.Equal(SensitiveProperties.RedactedPlaceholder, resultRow[nameof(Traveler.Email)]);
    }
}
