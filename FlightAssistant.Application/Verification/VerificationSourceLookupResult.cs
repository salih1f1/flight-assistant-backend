namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// What one source answered for one entity's lookup: the row it found, normalised to our
    /// checked-column names (<see cref="VerificationCheckedColumns.GetColumns"/>), or nothing.
    /// <see cref="VerificationComparer"/> is this type's only consumer.
    ///
    /// <para>
    /// <b>Ownership (#367 task boundary).</b> Task 3's <c>IVerificationSource</c> returns exactly
    /// this type from a successful fetch, and adds its own source-error case in its own return
    /// type -- an error is not a lookup result and is deliberately not modelled here. By the time a
    /// request reaches the comparer every source it names has already answered <see cref="Found"/>
    /// or <see cref="NotFound"/>: a source error sends the whole request back for retry before the
    /// comparer ever runs (spec §4.2's "Job outcomes" table), so this type never needs to represent one.
    /// </para>
    /// </summary>
    public sealed class VerificationSourceLookupResult
    {
        /// <summary>Checked column name (e.g. <c>Iata</c>, <c>NumericCode</c>) to the value the
        /// source returned for it, exactly as the source gave it -- before
        /// <see cref="VerificationValueNormalizer"/>'s matching-rule normalisation, so a
        /// <c>differences</c> entry shows what was actually read, not the folded form used only for
        /// equality. Null only when <see cref="IsFound"/> is false.</summary>
        public IReadOnlyDictionary<string, string?>? Values { get; }

        /// <summary>True when the source's dataset has a row for this entity.</summary>
        public bool IsFound => Values is not null;

        private VerificationSourceLookupResult(IReadOnlyDictionary<string, string?>? values) => Values = values;

        /// <summary>The source found a row. <paramref name="values"/> should carry every checked
        /// column for the entity type (see <see cref="VerificationCheckedColumns.GetColumns"/>) --
        /// a column the source could not populate should still be present, mapped to null or empty,
        /// which the comparer treats as an empty value, never as "column not checked".</summary>
        public static VerificationSourceLookupResult Found(IReadOnlyDictionary<string, string?> values) =>
            new(values ?? throw new ArgumentNullException(nameof(values)));

        /// <summary>The source's dataset loaded but has no row for this entity -- distinct from a
        /// source error (the dataset itself failing to load), which Task 3 models separately.</summary>
        public static readonly VerificationSourceLookupResult NotFound = new(null);
    }

    /// <summary>One requested source's name paired with what it answered, in request order -- the
    /// order <see cref="VerificationComparer"/> lists sources in a multi-source mismatch summary,
    /// and the order <c>VerificationRequest.Sources</c> (Domain) itself preserves.</summary>
    public sealed record VerificationSourceResult(string SourceName, VerificationSourceLookupResult Result);
}
