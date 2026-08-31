using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Exceptions;

namespace FlightAssistant.Domain.Configuration.Entities
{
    /// <summary>
    /// One metered call to the FlightAware AeroAPI. Written by the outbound HTTP pipeline (see
    /// Task 2) so usage can be rolled up by cost and endpoint without re-deriving it from logs.
    /// </summary>
    public sealed class AeroApiCall : BaseEntity
    {
        /// <summary>
        /// A low-cardinality endpoint template (e.g. <c>/flights/{ident}</c>), never a raw URL —
        /// raw paths carry flight idents and date ranges that would make grouping useless and could
        /// leak data into the admin view. The canonical list of templates is owned by Task 2.
        /// </summary>
        public string EndpointTemplate { get; private set; } = default!;
        public string Method { get; private set; } = default!;

        /// <summary>Null when the request never got a response (e.g. it was throttled locally or timed out).</summary>
        public int? StatusCode { get; private set; }
        public long DurationMs { get; private set; }
        public AeroApiCallOutcome Outcome { get; private set; }

        /// <summary>
        /// The endpoint's <i>conventional consumer</i> (<c>search</c>, <c>status</c>,
        /// <c>timezone-lookup</c>), not the call site that actually issued the request. It is
        /// derived from the endpoint template alone — one endpoint always yields one caller label —
        /// so it is a readable alias for the endpoint, and carries no information about which
        /// handler, request or feature the call came from. Anything wanting that would have to be
        /// passed in and stored separately.
        /// </summary>
        public string Caller { get; private set; } = default!;
        public DateTime CalledUtc { get; private set; }

        private AeroApiCall() { } // EF

        private AeroApiCall(
            string endpointTemplate,
            string method,
            int? statusCode,
            long durationMs,
            AeroApiCallOutcome outcome,
            string caller,
            DateTime calledUtc)
        {
            EndpointTemplate = endpointTemplate;
            Method = method;
            StatusCode = statusCode;
            DurationMs = durationMs;
            Outcome = outcome;
            Caller = caller;
            CalledUtc = calledUtc;
        }

        public static AeroApiCall Record(
            string endpointTemplate,
            string method,
            int? statusCode,
            long durationMs,
            AeroApiCallOutcome outcome,
            string caller,
            DateTime calledUtc)
        {
            if (string.IsNullOrWhiteSpace(endpointTemplate))
                throw new DomainException("Endpoint template is required.");
            if (string.IsNullOrWhiteSpace(method))
                throw new DomainException("Method is required.");
            if (string.IsNullOrWhiteSpace(caller))
                throw new DomainException("Caller is required.");
            if (durationMs < 0)
                throw new DomainException("Duration must not be negative.");

            return new AeroApiCall(
                endpointTemplate.Trim(),
                method.Trim().ToUpperInvariant(),
                statusCode,
                durationMs,
                outcome,
                caller.Trim(),
                calledUtc);
        }
    }

    public enum AeroApiCallOutcome
    {
        Success,
        Throttled,
        Failed
    }
}
