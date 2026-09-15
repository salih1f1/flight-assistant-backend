using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common;

namespace FlightAssistant.Application.Tests.Verification;

/// <summary>
/// The summary text and its 500-character truncation (#367 task 2, spec §4.2). The truncation
/// limit is asserted against <see cref="VerificationGuard.MaxIssueLength"/> directly rather than a
/// literal 500, so this test fails loudly if the two ever drift apart instead of quietly checking
/// the wrong number.
/// </summary>
public class VerificationSummaryBuilderTests
{
    [Fact]
    public void BuildMismatchSummary_SingleSourceSingleColumn_MatchesExpectedFormat()
    {
        var differences = new[] { new VerificationDifference("Name", "Heathrow", "Heathrow Airport", "ourairports") };

        Assert.Equal("Name differ (ourairports)", VerificationSummaryBuilder.BuildMismatchSummary(differences));
    }

    [Fact]
    public void BuildMismatchSummary_SingleSourceMultipleColumns_JoinsColumnsWithComma()
    {
        var differences = new[]
        {
            new VerificationDifference("Name", "Heathrow", "Heathrow Airport", "ourairports"),
            new VerificationDifference("City", "London", "Greater London", "ourairports")
        };

        Assert.Equal("Name, City differ (ourairports)", VerificationSummaryBuilder.BuildMismatchSummary(differences));
    }

    [Fact]
    public void BuildMismatchSummary_MultipleSources_OnePhrasePerSource_JoinedBySemicolon()
    {
        var differences = new[]
        {
            new VerificationDifference("Name", "Heathrow", "Heathrow Airport", "ourairports"),
            new VerificationDifference("Region", "Europe", "EMEA", "aeroapi")
        };

        Assert.Equal("Name differ (ourairports); Region differ (aeroapi)", VerificationSummaryBuilder.BuildMismatchSummary(differences));
    }

    [Fact]
    public void BuildMismatchSummary_Empty_Throws() =>
        Assert.Throws<ArgumentException>(() => VerificationSummaryBuilder.BuildMismatchSummary(Array.Empty<VerificationDifference>()));

    [Fact]
    public void BuildNotFoundSummary_SingleSource_MatchesExpectedFormat() =>
        Assert.Equal("Not found in ourairports", VerificationSummaryBuilder.BuildNotFoundSummary(new[] { "ourairports" }));

    [Fact]
    public void BuildNotFoundSummary_SeveralSources_CommaJoinedInOrder() =>
        Assert.Equal("Not found in ourairports, aeroapi", VerificationSummaryBuilder.BuildNotFoundSummary(new[] { "ourairports", "aeroapi" }));

    [Fact]
    public void BuildNotFoundSummary_Empty_Throws() =>
        Assert.Throws<ArgumentException>(() => VerificationSummaryBuilder.BuildNotFoundSummary(Array.Empty<string>()));

    [Fact]
    public void Truncate_ExactlyAtLimit_ReturnedUnchanged()
    {
        var text = new string('a', VerificationGuard.MaxIssueLength);

        var result = VerificationSummaryBuilder.Truncate(text);

        Assert.Equal(VerificationGuard.MaxIssueLength, result.Length);
        Assert.Equal(text, result);
        Assert.DoesNotContain('…', result);
    }

    [Fact]
    public void Truncate_OneOverLimit_TruncatedTo500WithTrailingEllipsis()
    {
        var text = new string('a', VerificationGuard.MaxIssueLength + 1);

        var result = VerificationSummaryBuilder.Truncate(text);

        Assert.Equal(VerificationGuard.MaxIssueLength, result.Length);
        Assert.EndsWith("…", result);
        Assert.Equal(new string('a', VerificationGuard.MaxIssueLength - 1) + "…", result);
    }

    [Fact]
    public void Truncate_WayOverLimit_StillExactly500WithTrailingEllipsis()
    {
        var text = new string('a', VerificationGuard.MaxIssueLength * 3);

        var result = VerificationSummaryBuilder.Truncate(text);

        Assert.Equal(VerificationGuard.MaxIssueLength, result.Length);
        Assert.EndsWith("…", result);
    }

    /// <summary>A mismatch summary built from enough differing columns to blow past 500
    /// characters is truncated the same way -- exercised through the public building method, not
    /// just <see cref="VerificationSummaryBuilder.Truncate"/> directly.</summary>
    [Fact]
    public void BuildMismatchSummary_LongEnoughToExceedLimit_IsTruncated()
    {
        var differences = Enumerable.Range(0, 100)
            .Select(i => new VerificationDifference($"Column{i:D3}", "a", "b", "ourairports"))
            .ToList();

        var summary = VerificationSummaryBuilder.BuildMismatchSummary(differences);

        Assert.True(summary.Length <= VerificationGuard.MaxIssueLength);
        Assert.Equal(VerificationGuard.MaxIssueLength, summary.Length);
        Assert.EndsWith("…", summary);
    }

    /// <summary>The surrogate-pair safety <c>VerificationRequest.Truncate</c> (Domain) also
    /// applies: a cut must never land inside a two-char UTF-16 surrogate pair.</summary>
    [Fact]
    public void Truncate_CutPointWouldSplitASurrogatePair_BacksUpOneCharacter()
    {
        // An astral character (surrogate pair) placed so the naive cut point (index 499)
        // would land on its low surrogate.
        var prefix = new string('a', VerificationGuard.MaxIssueLength - 2);
        var text = prefix + "\U0001F600" + "b"; // 😀 is 2 UTF-16 chars; total length = 500 + 1

        var result = VerificationSummaryBuilder.Truncate(text);

        // Backing off by one character to avoid splitting the pair can land one character short
        // of the limit -- still well within it, which is the actual guarantee ("never exceeds").
        Assert.True(result.Length <= VerificationGuard.MaxIssueLength);
        Assert.EndsWith("…", result);
        // The lone high surrogate must never appear immediately before the ellipsis.
        Assert.False(char.IsHighSurrogate(result[^2]));
    }
}
