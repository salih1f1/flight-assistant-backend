using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Verification
{
    /// <summary>Column-comparison outcome of checking our reference-data row against one or more
    /// sources (spec §3, §4.2's "Job outcomes" table): every requested source matched every checked
    /// column, at least one column differed on a source that did answer, or at least one source had
    /// no row at all -- which takes precedence over any mismatch from a source that did answer.
    /// Distinct from <c>VerificationRequestStatus</c> (Domain), which also carries the queue's own
    /// non-comparison states (<c>Pending</c>, <c>Running</c>, <c>Error</c>).</summary>
    public enum VerificationComparisonOutcome
    {
        Verified,
        Mismatch,
        NotFound
    }

    /// <summary>One column that differed for one source: <c>{ column, ours, source, sourceName }</c>
    /// in <c>verification_requests.differences</c> (spec §2.3). <see cref="Ours"/>/<see cref="Source"/>
    /// carry the raw values as given -- before matching-rule normalisation -- so the stored JSON
    /// shows what was actually compared, not the folded form used only for equality.</summary>
    public sealed record VerificationDifference(string Column, string? Ours, string? Source, string SourceName);

    /// <summary>
    /// The result of <see cref="VerificationComparer.Compare"/>: the outcome, the differing columns
    /// (populated only for <see cref="VerificationComparisonOutcome.Mismatch"/> -- empty for the
    /// other two outcomes, matching that <c>VerificationRequest.MarkNotFound</c> takes no differences
    /// argument), what each source that answered returned keyed by source name (a source that came
    /// back <see cref="VerificationSourceLookupResult.NotFound"/> contributes no entry, since it has
    /// no values -- this is "the sources that answered" from the job outcomes table), and a
    /// human-readable summary (null for <see cref="VerificationComparisonOutcome.Verified"/>, which
    /// needs none since <c>VerificationRequest.MarkVerified</c> takes no issue).
    /// </summary>
    public sealed record VerificationComparisonResult(
        VerificationComparisonOutcome Outcome,
        IReadOnlyList<VerificationDifference> Differences,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> SourceValues,
        string? Summary);

    /// <summary>
    /// Compares our reference-data row against one or more sources' lookup results (spec §3.1,
    /// §4.2). Pure: no EF, no HTTP, no clock -- callers own loading the entity, projecting it via
    /// <see cref="VerificationCheckedColumns"/>, fetching each source (Task 3), and persisting the
    /// result (Task 4).
    /// </summary>
    public static class VerificationComparer
    {
        /// <summary>Latitude/Longitude match within this many degrees (spec §3.1): exactly 0.01 is
        /// a match, 0.0101 is not. A named constant, not configuration, per spec §3.1.</summary>
        public const double CoordinateToleranceDegrees = 0.01;

        /// <summary>ElevationFt matches within this many feet (spec §3.1): exactly 50 is a match,
        /// 51 is not. A named constant, not configuration, per spec §3.1.</summary>
        public const double ElevationToleranceFt = 50;

        /// <summary>
        /// Compares <paramref name="ourValues"/> (see <see cref="VerificationCheckedColumns"/>'s
        /// <c>Project</c> overloads) against every source in <paramref name="sources"/>, over the
        /// columns <see cref="VerificationCheckedColumns.GetColumns"/> lists for
        /// <paramref name="entityType"/>. Any key in either side's record that is not one of those
        /// checked columns is never read -- an unchecked column (e.g. <c>AllianceCode</c>) is
        /// ignored by construction, not by an exclusion check.
        ///
        /// <para>
        /// <b>Precedence (spec §3.1).</b> If any source in <paramref name="sources"/> came back
        /// <see cref="VerificationSourceLookupResult.NotFound"/>, the outcome is
        /// <see cref="VerificationComparisonOutcome.NotFound"/> regardless of what any other source
        /// reported -- a missing row outranks a mismatch from a source that did answer. Only when
        /// every source was found: any differing checked column on any source makes it
        /// <see cref="VerificationComparisonOutcome.Mismatch"/>; otherwise
        /// <see cref="VerificationComparisonOutcome.Verified"/>.
        /// </para>
        /// </summary>
        /// <param name="ourValues">Our own checked-column values, keyed by column name.</param>
        /// <param name="sources">At least one requested source, in request order -- the order a
        /// multi-source mismatch summary lists sources in.</param>
        /// <exception cref="ArgumentException"><paramref name="sources"/> is empty.</exception>
        public static VerificationComparisonResult Compare(
            VerificationEntityType entityType,
            IReadOnlyDictionary<string, string?> ourValues,
            IReadOnlyList<VerificationSourceResult> sources)
        {
            ArgumentNullException.ThrowIfNull(ourValues);
            ArgumentNullException.ThrowIfNull(sources);
            if (sources.Count == 0)
                throw new ArgumentException("At least one source result is required.", nameof(sources));

            var columns = VerificationCheckedColumns.GetColumns(entityType);

            var sourceValues = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
            var missingSources = new List<string>();
            var differences = new List<VerificationDifference>();

            foreach (var source in sources)
            {
                if (!source.Result.IsFound)
                {
                    missingSources.Add(source.SourceName);
                    continue;
                }

                var values = source.Result.Values!;
                sourceValues[source.SourceName] = values;

                foreach (var column in columns)
                {
                    ourValues.TryGetValue(column.Name, out var ours);
                    values.TryGetValue(column.Name, out var sourceValue);

                    if (!ColumnsMatch(column.Kind, ours, sourceValue))
                        differences.Add(new VerificationDifference(column.Name, ours, sourceValue, source.SourceName));
                }
            }

            // Missing takes precedence over mismatch -- checked first for exactly that reason
            // (break-proof: swapping this ordering is what the precedence test catches).
            if (missingSources.Count > 0)
            {
                var summary = VerificationSummaryBuilder.BuildNotFoundSummary(missingSources);
                return new VerificationComparisonResult(
                    VerificationComparisonOutcome.NotFound, Array.Empty<VerificationDifference>(), sourceValues, summary);
            }

            if (differences.Count > 0)
            {
                var summary = VerificationSummaryBuilder.BuildMismatchSummary(differences);
                return new VerificationComparisonResult(VerificationComparisonOutcome.Mismatch, differences, sourceValues, summary);
            }

            return new VerificationComparisonResult(
                VerificationComparisonOutcome.Verified, Array.Empty<VerificationDifference>(), sourceValues, null);
        }

        /// <summary>
        /// Whether two values of one checked column are equal under that column's matching rule
        /// (spec §3.1). The empty-values rule applies uniformly before any kind-specific rule: both
        /// sides empty is a match, only one side empty is not, regardless of column kind.
        ///
        /// <para>
        /// Public so accept-source (#378) decides "has this column changed since the check" by the
        /// same rule the check itself used, rather than by raw string equality.
        /// </para>
        /// </summary>
        public static bool ColumnsMatch(VerificationColumnKind kind, string? ours, string? source)
        {
            var oursEmpty = VerificationValueNormalizer.IsEmpty(ours);
            var sourceEmpty = VerificationValueNormalizer.IsEmpty(source);
            if (oursEmpty && sourceEmpty) return true;
            if (oursEmpty || sourceEmpty) return false;

            return kind switch
            {
                VerificationColumnKind.Code =>
                    string.Equals(
                        VerificationValueNormalizer.NormalizeCode(ours!),
                        VerificationValueNormalizer.NormalizeCode(source!),
                        StringComparison.Ordinal),

                VerificationColumnKind.Text =>
                    string.Equals(
                        VerificationValueNormalizer.NormalizeText(ours!),
                        VerificationValueNormalizer.NormalizeText(source!),
                        StringComparison.Ordinal),

                // Neither side parsing is a mismatch, not an exception: malformed numeric data
                // from a source is a real disagreement worth surfacing, not a reason to abort the check.
                VerificationColumnKind.NumericCode =>
                    VerificationValueNormalizer.TryParseNumericCode(ours!, out var oursInt)
                    && VerificationValueNormalizer.TryParseNumericCode(source!, out var sourceInt)
                    && oursInt == sourceInt,

                VerificationColumnKind.Coordinate =>
                    VerificationValueNormalizer.TryParseNumber(ours!, out var oursCoordinate)
                    && VerificationValueNormalizer.TryParseNumber(source!, out var sourceCoordinate)
                    && Math.Abs(oursCoordinate - sourceCoordinate) <= CoordinateToleranceDegrees,

                VerificationColumnKind.Elevation =>
                    VerificationValueNormalizer.TryParseNumber(ours!, out var oursElevation)
                    && VerificationValueNormalizer.TryParseNumber(source!, out var sourceElevation)
                    && Math.Abs(oursElevation - sourceElevation) <= ElevationToleranceFt,

                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown column kind.")
            };
        }
    }
}
