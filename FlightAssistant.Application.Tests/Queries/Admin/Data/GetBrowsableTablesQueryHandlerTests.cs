using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Data;
using FlightAssistant.Domain.Travelers.Entities;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Data;

/// <summary>
/// <see cref="GetBrowsableTablesQueryHandler"/>'s column description (#485).
///
/// <para>
/// These run the real handler rather than calling <c>BrowsableColumnKinds.Describe</c> themselves.
/// A test that describes a column with <c>SensitiveProperties.IsSensitive(...)</c> and then asserts
/// the result equals <c>SensitiveProperties.IsSensitive(...)</c> compares a value with itself: it
/// passes whatever the handler does, including passing <c>false</c> for every column. That exact
/// break was tried against an earlier version of this suite and went unnoticed, which is why the
/// check lives here, on the handler's own output.
/// </para>
/// </summary>
public class GetBrowsableTablesQueryHandlerTests
{
    private readonly IBrowsableDataRepository _repository = Substitute.For<IBrowsableDataRepository>();
    private readonly GetBrowsableTablesQueryHandler _handler;

    public GetBrowsableTablesQueryHandlerTests() => _handler = new GetBrowsableTablesQueryHandler(_repository);

    private async Task<BrowsableColumn> ColumnAsync(string slug, string name)
    {
        var summaries = await _handler.Handle(new GetBrowsableTablesQuery(), CancellationToken.None);
        return summaries.Single(s => s.Slug == slug).Columns.Single(c => c.Name == name);
    }

    [Fact]
    public async Task AMaskedColumn_IsPublishedAsSensitive()
    {
        var email = await ColumnAsync("travelers", nameof(Traveler.Email));

        Assert.True(email.Sensitive);
    }

    /// <summary>
    /// Sensitivity is a separate fact from the column's shape: the kind stays the real type, so a
    /// client can still tell a masked date from a masked string.
    /// </summary>
    [Fact]
    public async Task AMaskedColumn_KeepsItsRealKind()
    {
        var email = await ColumnAsync("travelers", nameof(Traveler.Email));

        Assert.Equal(BrowsableColumnKind.Text, email.Kind);
    }

    [Fact]
    public async Task AnOrdinaryColumn_IsNotPublishedAsSensitive()
    {
        var displayName = await ColumnAsync("travelers", nameof(Traveler.DisplayName));

        Assert.False(displayName.Sensitive);
    }

    /// <summary>
    /// Across every table: what is published as sensitive is exactly what is masked. One source of
    /// truth, so the flag cannot drift from the masking it describes.
    /// </summary>
    [Fact]
    public async Task EveryPublishedColumn_AgreesWithWhatIsActuallyMasked()
    {
        var summaries = await _handler.Handle(new GetBrowsableTablesQuery(), CancellationToken.None);

        foreach (var table in BrowsableTables.All)
        {
            var published = summaries.Single(s => s.Slug == table.Slug);
            foreach (var column in published.Columns)
            {
                Assert.Equal(
                    SensitiveProperties.IsSensitive(table.EntityType, column.Name),
                    column.Sensitive);
            }
        }
    }

    /// <summary>
    /// The suite would be hollow if no table actually had a masked column — every assertion above
    /// would hold against a handler that published <c>false</c> everywhere.
    /// </summary>
    [Fact]
    public async Task AtLeastOneColumn_IsActuallySensitive()
    {
        var summaries = await _handler.Handle(new GetBrowsableTablesQuery(), CancellationToken.None);

        Assert.Contains(summaries.SelectMany(s => s.Columns), column => column.Sensitive);
    }
}
