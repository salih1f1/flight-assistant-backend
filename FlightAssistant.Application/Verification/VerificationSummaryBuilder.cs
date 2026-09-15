using FlightAssistant.Domain.Common;

namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// Builds <c>VerificationIssue</c> -- the human-readable summary the job passes to
    /// <c>MarkVerificationFailed</c> for a <see cref="VerificationComparisonOutcome.Mismatch"/> or
    /// <see cref="VerificationComparisonOutcome.NotFound"/> outcome (spec §4.2) -- and truncates it
    /// to fit <see cref="VerificationGuard.MaxIssueLength"/>, the domain guard's own limit, so the
    /// job never hands the domain a summary it would reject with <see cref="ArgumentException"/>.
    /// </summary>
    public static class VerificationSummaryBuilder
    {
        /// <summary>The domain's <c>verification_issue</c> column size -- see
        /// <see cref="VerificationGuard.MaxIssueLength"/>. Kept as one alias here rather than a
        /// second literal <c>500</c>, so a future column-size change is picked up automatically
        /// instead of the two silently drifting apart.</summary>
        public const int MaxLength = VerificationGuard.MaxIssueLength;

        private const string Ellipsis = "…";

        /// <summary>
        /// E.g. <c>"Name, City differ (ourairports)"</c>: one phrase per source that had at least
        /// one differing column, that source's columns comma-joined in checked-column order,
        /// phrases joined with <c>"; "</c> in the order sources first appear in
        /// <paramref name="differences"/> -- so a source that fully matched contributes no phrase
        /// and is not named. Truncated to <see cref="MaxLength"/> characters, trailing <c>"…"</c>
        /// counted inside that budget.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="differences"/> is empty.</exception>
        public static string BuildMismatchSummary(IReadOnlyList<VerificationDifference> differences)
        {
            ArgumentNullException.ThrowIfNull(differences);
            if (differences.Count == 0)
                throw new ArgumentException("At least one difference is required.", nameof(differences));

            // GroupBy preserves both the order groups are first seen in and each group's internal
            // element order, which is what keeps "naming only that source" (and multi-source
            // ordering) deterministic without a hand-rolled ordered-grouping loop.
            var phrases = differences
                .GroupBy(d => d.SourceName, StringComparer.Ordinal)
                .Select(g => $"{string.Join(", ", g.Select(d => d.Column))} differ ({g.Key})");

            return Truncate(string.Join("; ", phrases));
        }

        /// <summary>E.g. <c>"Not found in ourairports"</c>, or <c>"Not found in ourairports, aeroapi"</c>
        /// for several missing sources, in <paramref name="missingSourceNames"/>'s order. Truncated
        /// like <see cref="BuildMismatchSummary"/>.</summary>
        /// <exception cref="ArgumentException"><paramref name="missingSourceNames"/> is empty.</exception>
        public static string BuildNotFoundSummary(IReadOnlyList<string> missingSourceNames)
        {
            ArgumentNullException.ThrowIfNull(missingSourceNames);
            if (missingSourceNames.Count == 0)
                throw new ArgumentException("At least one missing source is required.", nameof(missingSourceNames));

            return Truncate($"Not found in {string.Join(", ", missingSourceNames)}");
        }

        /// <summary>
        /// Truncates to <see cref="MaxLength"/> characters, replacing anything past that with a
        /// trailing <c>"…"</c> that counts inside the budget -- so the result is never longer than
        /// <see cref="MaxLength"/>, and never cuts between the two halves of a surrogate pair, the
        /// same rule <c>VerificationRequest.Truncate</c> (Domain) applies to <c>error</c>.
        /// </summary>
        public static string Truncate(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (text.Length <= MaxLength) return text;

            var cutoff = MaxLength - Ellipsis.Length;
            if (cutoff > 0 && char.IsHighSurrogate(text[cutoff - 1])) cutoff--;
            return text[..cutoff] + Ellipsis;
        }
    }
}
