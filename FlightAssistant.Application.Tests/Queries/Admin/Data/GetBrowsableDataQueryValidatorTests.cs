using FlightAssistant.Application.Queries.Admin.Data;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Domain.Travelers.Entities;

namespace FlightAssistant.Application.Tests.Queries.Admin.Data;

/// <summary>
/// <see cref="GetBrowsableDataQueryValidator"/> with several criteria in play (#476). Each rule is
/// per-criterion, and each message names the criterion it is about: "Invalid filter column." alone
/// does not say which of three filters to fix, and fixing them one round-trip at a time is the
/// experience that produces.
/// </summary>
public class GetBrowsableDataQueryValidatorTests
{
    private readonly GetBrowsableDataQueryValidator _validator = new();

    private static GetBrowsableDataQuery Query(params string[] filters) =>
        new("travelers", new PagedRequest(), Filters: filters);

    [Fact]
    public void SeveralValidCriteria_AreValid()
    {
        var result = _validator.Validate(Query(
            $"{nameof(Traveler.DisplayName)}:ada",
            $"{nameof(Traveler.CreatedUtc)}:>=2026-09-01"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void NoFiltersAtAll_IsValid()
    {
        Assert.True(_validator.Validate(new GetBrowsableDataQuery("travelers", new PagedRequest())).IsValid);
    }

    [Fact]
    public void OneCriterionWithNoColon_IsRejected_AndTheMessageNamesIt()
    {
        var result = _validator.Validate(Query($"{nameof(Traveler.DisplayName)}:ada", "NotAFilter"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("NotAFilter"));
    }

    [Fact]
    public void OneCriterionNamingAnUnknownColumn_IsRejected_AndTheMessageNamesThatColumn()
    {
        var result = _validator.Validate(Query($"{nameof(Traveler.DisplayName)}:ada", "Cuntry:TR"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("Cuntry"));
    }

    /// <summary>
    /// A sensitive column stays unfilterable however many criteria surround it — a filter is a way
    /// to guess a masked value one character at a time, which is why the rule exists at all.
    /// </summary>
    [Fact]
    public void ACriterionOnASensitiveColumn_IsRejected()
    {
        var result = _validator.Validate(Query(
            $"{nameof(Traveler.DisplayName)}:ada",
            $"{nameof(Traveler.Email)}:a"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("sensitive"));
    }

    /// <summary>
    /// Two bad criteria produce two complaints, not one: being told about the second only after
    /// fixing the first is the round-trip this avoids.
    /// </summary>
    [Fact]
    public void EveryFailingCriterion_IsReported()
    {
        var result = _validator.Validate(Query("Cuntry:TR", "Naem:air"));

        Assert.False(result.IsValid);
        var message = string.Join(" ", result.Errors.Select(e => e.ErrorMessage));
        Assert.Contains("Cuntry", message);
        Assert.Contains("Naem", message);
    }

    [Fact]
    public void TheMaximumNumberOfCriteria_IsValid()
    {
        var filters = Enumerable.Range(0, GetBrowsableDataQuery.MaxFilters)
            .Select(i => $"{nameof(Traveler.DisplayName)}:{i}")
            .ToArray();

        Assert.True(_validator.Validate(Query(filters)).IsValid);
    }

    /// <summary>
    /// A bound on how much query one URL can compose. Nothing the filter form produces comes near
    /// it — this is about a hand-written URL, not about the panel.
    /// </summary>
    [Fact]
    public void MoreThanTheMaximumNumberOfCriteria_IsRejected()
    {
        var filters = Enumerable.Range(0, GetBrowsableDataQuery.MaxFilters + 1)
            .Select(i => $"{nameof(Traveler.DisplayName)}:{i}")
            .ToArray();

        var result = _validator.Validate(Query(filters));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains(GetBrowsableDataQuery.MaxFilters.ToString()));
    }

    /// <summary>
    /// Each criterion is split on its first colon only, so an ISO-8601 value keeps its own.
    /// </summary>
    [Fact]
    public void AValueContainingColons_KeepsThemAllInTheValue()
    {
        var query = Query($"{nameof(Traveler.CreatedUtc)}:2026-09-01T12:30:00Z");

        var parsed = Assert.Single(query.ParsedFilters);
        Assert.Equal(nameof(Traveler.CreatedUtc), parsed.Column);
        Assert.Equal("2026-09-01T12:30:00Z", parsed.Value);
    }

    [Fact]
    public void SeveralCriteria_AreParsedInTheOrderGiven()
    {
        var query = Query($"{nameof(Traveler.DisplayName)}:ada", $"{nameof(Traveler.DisplayName)}:love");

        Assert.Equal(
            [(nameof(Traveler.DisplayName), "ada"), (nameof(Traveler.DisplayName), "love")],
            query.ParsedFilters);
    }
}
