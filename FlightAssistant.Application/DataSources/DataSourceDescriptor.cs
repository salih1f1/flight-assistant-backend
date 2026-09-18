namespace FlightAssistant.Application.DataSources
{
    /// <summary>Whether a missing or failing source is a fault.</summary>
    public enum DataSourceCriticality { Required, Optional }

    /// <summary>How the health page groups source rows.</summary>
    public enum DataSourceKind { Database, FlightData, ReferenceData, Notifications, Telemetry, Infrastructure }

    /// <summary>
    /// One outside dependency, as the health page describes it. <see cref="WhatIsChecked"/> and
    /// <see cref="ExternalUrl"/> live here rather than on a health check because they are facts
    /// about the source itself, and because a single registry is what makes the catalog
    /// completeness test possible (see <c>DataSourceCatalogTests</c>).
    /// </summary>
    /// <param name="Key">Stable id, used in URLs and as the status table's primary key.</param>
    /// <param name="WhatIsChecked">One author-written sentence, rendered verbatim to the operator.</param>
    /// <param name="HttpClientName">The named <see cref="HttpClient"/> this source is reached
    /// through, or null for a source not reached over HTTP. The completeness test matches on this.</param>
    /// <param name="ProbeCostNote">Null when probing is free; otherwise the warning shown before
    /// the call goes out.</param>
    public sealed record DataSourceDescriptor(
        string Key,
        string DisplayName,
        DataSourceKind Kind,
        DataSourceCriticality Criticality,
        string WhatIsChecked,
        string? ExternalUrl,
        string? HttpClientName,
        bool SupportsProbe,
        string? ProbeCostNote);
}
