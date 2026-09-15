using System.Text.Json;

namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// JSON serialisation for the two <c>jsonb</c> columns <see cref="VerificationComparisonResult"/>
    /// feeds (spec §2.3): <c>differences</c> and <c>source_values</c>. camelCase, matching the
    /// documented shapes (<c>{ "column", "ours", "source", "sourceName" }</c> per difference; an
    /// object keyed by source name for <c>source_values</c>) and deterministic -- declared
    /// (positional) property order for <see cref="VerificationDifference"/>, checked-column
    /// -registry order within each source's own record (<see cref="VerificationCheckedColumns"/>
    /// builds every "ours"/source record in that order), and source-request order for the outer
    /// <c>source_values</c> object.
    ///
    /// <para>
    /// Task 4's processor calls these to build the strings <c>VerificationRequest.MarkMismatch</c>
    /// and <c>MarkNotFound</c> (Domain) store; nothing here talks to EF or knows about <c>jsonb</c>
    /// as a database type -- both are plain strings from this file's point of view.
    /// </para>
    /// </summary>
    public static class VerificationJson
    {
        /// <summary>camelCase property names, no indentation -- the stored form is a compact
        /// <c>jsonb</c> value, not a document meant to be read unformatted.</summary>
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

        /// <summary>Serialises <see cref="VerificationComparisonResult.Differences"/> to the
        /// <c>differences</c> column's JSON array shape.</summary>
        public static string SerializeDifferences(IReadOnlyList<VerificationDifference> differences) =>
            JsonSerializer.Serialize(differences, Options);

        /// <summary>Serialises <see cref="VerificationComparisonResult.SourceValues"/> to the
        /// <c>source_values</c> column's JSON object shape.</summary>
        public static string SerializeSourceValues(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> sourceValues) =>
            JsonSerializer.Serialize(sourceValues, Options);
    }
}
