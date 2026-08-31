namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// The low-cardinality endpoint template a metered AeroAPI request is booked under, together
    /// with the caller label derived from the <i>same</i> match — one mapping, so the two can never
    /// drift apart.
    ///
    /// <para>
    /// A raw request path must never reach a usage row: it carries flight idents and date ranges,
    /// which would both explode the cardinality of the per-endpoint rollup and put request data in
    /// the admin view. The three templates below are the canonical list referenced by
    /// <see cref="FlightAssistant.Domain.Configuration.Entities.AeroApiCall.EndpointTemplate"/> and
    /// used as the keys of <see cref="AeroApiOptions.Pricing"/>.
    /// </para>
    /// </summary>
    public readonly record struct AeroApiEndpointTemplate(string Template, string Caller)
    {
        public const string Schedules = "/schedules/{range}";
        public const string Flights = "/flights/{ident}";
        public const string Airports = "/airports/{id}";

        /// <summary>Bucket for a path whose endpoint family cannot be named safely.</summary>
        public const string Unrecognised = "/{...}";

        private const string UnknownCaller = "unknown";

        /// <summary>
        /// The root segment of FlightAware's own API path (<c>…/aeroapi/</c>), hardcoded rather
        /// than read from <see cref="AeroApiOptions.BaseUrl"/> — which is configurable, so a base
        /// URL rooted anywhere else simply will not match here. That costs nothing: skipping this
        /// segment only stops every unrecognised endpoint from collapsing into a single
        /// <c>/aeroapi/{...}</c> bucket that names nothing, and it is skipped by name, not by
        /// position, so an unmatched root just leaves the first segment as the family name.
        /// </summary>
        private const string ApiRootSegment = "aeroapi";

        /// <summary>
        /// Upper bound on the family segment kept for an unrecognised endpoint.
        /// <see cref="FlightAssistant.Domain.Configuration.Entities.AeroApiCall.EndpointTemplate"/>
        /// is <c>varchar(128)</c>; an over-long segment would throw at insert, and
        /// <see cref="AeroApiUsageRecordingHandler"/>'s best-effort catch would swallow that and
        /// silently drop the usage row — losing exactly the spend record metering exists to keep.
        /// No AeroAPI endpoint name comes near this length, so anything longer is data wearing an
        /// endpoint name's clothes and is bucketed as <see cref="Unrecognised"/> instead.
        /// </summary>
        private const int MaxFamilySegmentLength = 32;

        /// <summary>
        /// Maps a request URI to its template and caller. Matching is by path segment, in path
        /// order, so the host and the API root prefix are irrelevant and query strings — which
        /// carry origins, dates and idents — are never looked at at all.
        /// </summary>
        public static AeroApiEndpointTemplate From(Uri? requestUri)
        {
            if (requestUri is null) return new AeroApiEndpointTemplate(Unrecognised, UnknownCaller);

            var segments = requestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                switch (segment.ToLowerInvariant())
                {
                    case "schedules": return new AeroApiEndpointTemplate(Schedules, "search");
                    case "flights": return new AeroApiEndpointTemplate(Flights, "status");
                    case "airports": return new AeroApiEndpointTemplate(Airports, "timezone-lookup");
                }
            }

            // Unrecognised endpoint: keep the family segment (the one after the API root) and drop
            // everything after it, since that is where idents and ranges live. The letters-only and
            // length guards are the backstop — if the segment does not read as an endpoint name, it
            // is treated as data and dropped entirely rather than stored.
            var family = Array.Find(segments, s => !s.Equals(ApiRootSegment, StringComparison.OrdinalIgnoreCase));

            return family is not null && family.Length <= MaxFamilySegmentLength && family.All(char.IsAsciiLetter)
                ? new AeroApiEndpointTemplate($"/{family.ToLowerInvariant()}/{{...}}", UnknownCaller)
                : new AeroApiEndpointTemplate(Unrecognised, UnknownCaller);
        }
    }
}
