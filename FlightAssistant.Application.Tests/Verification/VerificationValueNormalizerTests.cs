using FlightAssistant.Application.Verification;

namespace FlightAssistant.Application.Tests.Verification;

/// <summary>
/// Unit tests for the normalisation rules on their own, isolated from the comparer's
/// empty-value/precedence logic (#367 task 2, spec §3.1).
/// </summary>
public class VerificationValueNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsEmpty_NullEmptyOrWhitespace_ReturnsTrue(string? value) =>
        Assert.True(VerificationValueNormalizer.IsEmpty(value));

    [Fact]
    public void IsEmpty_NonBlankValue_ReturnsFalse() =>
        Assert.False(VerificationValueNormalizer.IsEmpty("GB"));

    [Fact]
    public void NormalizeCode_TrimsAndUpperCases()
    {
        Assert.Equal("GB", VerificationValueNormalizer.NormalizeCode(" gb "));
        Assert.Equal("GB", VerificationValueNormalizer.NormalizeCode("GB"));
    }

    /// <summary>Turkish İ (U+0130) decomposes to I + a combining dot above (U+0307); stripping
    /// that non-spacing mark is what makes this equal a plain "Istanbul" -- see break-proof 2.</summary>
    [Fact]
    public void NormalizeText_StripsAccents_IstanbulWithDottedCapitalIEqualsPlainIstanbul()
    {
        var withDot = VerificationValueNormalizer.NormalizeText("İstanbul Airport");
        var plain = VerificationValueNormalizer.NormalizeText("Istanbul Airport");

        Assert.Equal(plain, withDot);
        Assert.Equal("ISTANBUL AIRPORT", plain);
    }

    /// <summary>Côte (circumflex accent) folds punctuation (the apostrophe) into a space just
    /// like the plain-ASCII side already has one.</summary>
    [Fact]
    public void NormalizeText_FoldsAccentsAndPunctuation_CoteDIvoireEqualsCoteDIvoireNoAccentNoApostrophe()
    {
        var accented = VerificationValueNormalizer.NormalizeText("Côte d'Ivoire");
        var plain = VerificationValueNormalizer.NormalizeText("Cote d Ivoire");

        Assert.Equal(plain, accented);
        Assert.Equal("COTE D IVOIRE", plain);
    }

    [Fact]
    public void NormalizeText_CollapsesWhitespace_AndTrims()
    {
        var result = VerificationValueNormalizer.NormalizeText("  Los   Angeles  ");
        Assert.Equal("LOS ANGELES", result);
    }

    [Theory]
    [InlineData("4", 4)]
    [InlineData("004", 4)]
    [InlineData(" 792 ", 792)]
    public void TryParseNumericCode_ParsesLeadingZerosAndWhitespace(string value, int expected)
    {
        Assert.True(VerificationValueNormalizer.TryParseNumericCode(value, out var result));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryParseNumericCode_NonInteger_ReturnsFalse() =>
        Assert.False(VerificationValueNormalizer.TryParseNumericCode("abc", out _));

    [Theory]
    [InlineData("40.9925", 40.9925)]
    [InlineData(" -0.5 ", -0.5)]
    public void TryParseNumber_ParsesInvariantCultureFloats(string value, double expected)
    {
        Assert.True(VerificationValueNormalizer.TryParseNumber(value, out var result));
        Assert.Equal(expected, result, precision: 10);
    }

    [Fact]
    public void TryParseNumber_NonNumeric_ReturnsFalse() =>
        Assert.False(VerificationValueNormalizer.TryParseNumber("abc", out _));
}
