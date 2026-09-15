using System.Globalization;
using System.Text;

namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// The value-normalisation half of the matching rules (spec §3.1): turning a raw column value
    /// -- ours or a source's -- into the canonical form <see cref="VerificationComparer"/> compares
    /// by equality, plus the numeric parsing the tolerance-based columns need. Kept separate from
    /// the comparer so each normalisation rule can be exercised against its exact boundary on its
    /// own, independent of the empty-value and precedence rules the comparer owns.
    /// </summary>
    public static class VerificationValueNormalizer
    {
        /// <summary>Null, empty or all-whitespace. The empty-values rule (spec §3.1: both empty is
        /// a match, one empty is not) is checked with this before any kind-specific comparison runs
        /// -- none of the methods below are meaningful on an empty value.</summary>
        public static bool IsEmpty(string? value) => string.IsNullOrWhiteSpace(value);

        /// <summary>A code column's canonical form: trimmed, then upper-cased (invariant), so
        /// <c>" gb "</c> and <c>"GB"</c> are equal. <paramref name="value"/> must not be null or
        /// empty -- check <see cref="IsEmpty"/> first.</summary>
        public static string NormalizeCode(string value) => value.Trim().ToUpperInvariant();

        /// <summary>
        /// A text column's canonical form (spec §3.1), applied in this order: Unicode
        /// <see cref="NormalizationForm.FormD"/> decomposition with non-spacing marks then removed
        /// (strips accents -- <c>İstanbul</c> and <c>Côte</c> lose their diacritics and become
        /// <c>Istanbul</c>/<c>Cote</c>), case-folded (<see cref="string.ToUpperInvariant"/>), every
        /// character that is not a letter or digit replaced with a space (folds punctuation into a
        /// word break -- <c>d'Ivoire</c> and <c>d Ivoire</c> normalise the same way), then runs of
        /// whitespace collapsed to one space and the result trimmed. <paramref name="value"/> must
        /// not be null or empty -- check <see cref="IsEmpty"/> first.
        /// </summary>
        public static string NormalizeText(string value)
        {
            var decomposed = value.Normalize(NormalizationForm.FormD);

            var withoutMarks = new StringBuilder(decomposed.Length);
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                withoutMarks.Append(ch);
            }

            var upper = withoutMarks.ToString().ToUpperInvariant();

            var spaced = new StringBuilder(upper.Length);
            foreach (var ch in upper)
                spaced.Append(char.IsLetterOrDigit(ch) ? ch : ' ');

            return CollapseWhitespace(spaced.ToString());
        }

        /// <summary>Parses a numeric-code column, tolerant of surrounding whitespace and leading
        /// zeros -- <c>"004"</c> parses to <c>4</c>, equal to a bare <c>"4"</c>. False for anything
        /// that is not a plain integer.</summary>
        public static bool TryParseNumericCode(string value, out int result) =>
            int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

        /// <summary>Parses a coordinate or elevation column as an invariant-culture
        /// floating-point number, tolerant of surrounding whitespace.</summary>
        public static bool TryParseNumber(string value, out double result) =>
            double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        // The non-letter/non-digit replacement step above only ever introduces literal ' ' (space)
        // characters, and any whitespace already in the input became one after upper-casing was
        // itself already just spaces post-replacement -- so collapsing here only ever needs to
        // merge runs of the single ' ' character, never tabs/newlines/etc.
        private static string CollapseWhitespace(string value)
        {
            var builder = new StringBuilder(value.Length);
            var lastWasSpace = false;
            foreach (var ch in value)
            {
                if (ch == ' ')
                {
                    if (!lastWasSpace) builder.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    builder.Append(ch);
                    lastWasSpace = false;
                }
            }

            return builder.ToString().Trim();
        }
    }
}
