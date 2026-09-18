using FlightAssistant.Application.DataSources;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.DataSources.Entities;

namespace FlightAssistant.Application.Queries.Admin.DataSources
{
    /// <summary>
    /// One catalog entry plus its stored row, as the panel sees it. Shared by the list query and by
    /// the probe command, which returns the row it just refreshed: two projections would let the
    /// row you get after pressing "Check now" differ from the one the page shows a second later.
    /// </summary>
    public static class DataSourceProjection
    {
        public static AdminDataSourceDto Map(
            DataSourceDescriptor source,
            DataSourceStatus? row,
            DateTimeOffset now,
            bool includeDiagnostics)
        {
            var lastOutcome = ParseOutcome(row?.LastOutcome);
            var status = DataSourceStatusEvaluator.Evaluate(
                lastOutcome,
                AsOffset(row?.LastOkUtc),
                source.Criticality,
                now);

            return new AdminDataSourceDto(
                source.Key,
                source.DisplayName,
                source.Kind.ToString(),
                source.Criticality.ToString(),
                status.ToString(),
                source.WhatIsChecked,
                source.ExternalUrl,
                // The stored string only when it is one this build knows: an outcome written by an
                // older build and since renamed must not reach the panel as a status it cannot read.
                lastOutcome?.ToString(),
                row?.LastOutcomeUtc,
                row?.LastOkUtc,
                includeDiagnostics ? row?.LastError : null,
                row?.ConsecutiveFailures ?? 0,
                source.SupportsProbe,
                source.ProbeCostNote,
                row?.LastProbeUtc,
                row?.LastProbeBy);
        }

        /// <summary>
        /// Null for a missing or unreadable stored value, which the evaluator reads as "never
        /// observed" — the honest answer for a row this build cannot interpret.
        /// </summary>
        private static DataSourceOutcome? ParseOutcome(string? stored) =>
            Enum.TryParse<DataSourceOutcome>(stored, out var outcome) ? outcome : null;

        private static DateTimeOffset? AsOffset(DateTime? utc) =>
            utc is null ? null : new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc));
    }
}
