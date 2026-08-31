using System.ComponentModel.DataAnnotations;
using FlightAssistant.Application.Interfaces.Configuration;
using FlightAssistant.Application.Interfaces.ExternalServices;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Configuration for the FlightAware AeroAPI client, bound from the <c>AeroApi</c> section.
    /// Registered with <c>ValidateDataAnnotations().ValidateOnStart()</c> so a missing/invalid
    /// value fails fast at startup rather than surfacing as a confusing runtime error on the
    /// first search. Implements <see cref="IFlightSearchHorizonOptions"/> and
    /// <see cref="IAeroApiUsagePricingOptions"/> so Application-layer consumers (the search
    /// validator, the usage-rollup handler) can read the real configured values without this
    /// (Infrastructure) project being referenced from Application — see the DI registrations in
    /// <c>FlightAssistant.Infrastructure.DependencyInjection</c>.
    /// </summary>
    public sealed class AeroApiOptions : IFlightSearchHorizonOptions, IAeroApiUsagePricingOptions
    {
        /// <summary>Sent as the <c>x-apikey</c> header on every request. Never logged.</summary>
        [Required]
        [MinLength(1)]
        public string ApiKey { get; set; } = string.Empty;

        public string BaseUrl { get; set; } = "https://aeroapi.flightaware.com/aeroapi/";

        /// <summary>Caps billable page fetches per search — a runaway paging loop is a billing risk
        /// on a metered tier.</summary>
        [Range(1, 20)]
        public int MaxPages { get; set; } = 4;

        /// <summary>
        /// Minimum spacing between outbound requests, enforced globally by
        /// <see cref="AeroApiThrottleGate"/>. The personal tier allows ~5 queries/minute.
        ///
        /// <para>
        /// The floor is deliberate: this is the only process-wide brake on a metered, per-call
        /// billed API, and a zero or negative value would silently disable it. The ceiling keeps
        /// <see cref="AttemptTimeout"/> — which is derived from this value — inside sane bounds.
        /// </para>
        /// </summary>
        [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
        public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromSeconds(12);

        [Range(1, 12)]
        public int MaxFutureMonths { get; set; } = 12;

        [Range(1, 3)]
        public int MaxPastMonths { get; set; } = 3;

        /// <summary>
        /// Network budget for a single outbound attempt, <b>excluding</b> the time that attempt may
        /// spend queued on <see cref="AeroApiThrottleGate"/>. The throttle wait is added on top —
        /// see <see cref="AttemptTimeout"/>.
        /// </summary>
        [Range(1, 300)]
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum retries the resilience pipeline may make for one logical request. Exposed here
        /// (rather than hardcoded at the registration site) so <see cref="TotalRequestTimeout"/>
        /// can be derived from it and cannot drift out of step with it.
        /// </summary>
        [Range(0, 5)]
        public int MaxRetryAttempts { get; set; } = 2;

        /// <summary>
        /// Per-attempt timeout for the resilience pipeline. <b>Must</b> exceed
        /// <see cref="MinRequestInterval"/>: <see cref="AeroApiThrottlingHandler"/> is registered
        /// <i>inside</i> the resilience handler (so retries are throttled too), which means the
        /// throttle's own wait of up to one whole interval elapses <i>within</i> this timeout. When
        /// the timeout was the library default of 10s and the interval 12s, every second call in an
        /// interval window was killed before it ever reached the transport, retried twice, and then
        /// failed the total-request timeout — a 30s hang reported as a 502. Deriving the value here
        /// means tuning <see cref="MinRequestInterval"/> can never silently reintroduce that.
        /// </summary>
        public TimeSpan AttemptTimeout => MinRequestInterval + TimeSpan.FromSeconds(TimeoutSeconds);

        /// <summary>
        /// Budget for one logical request including every retry: each attempt may burn a full
        /// <see cref="AttemptTimeout"/>, plus the pipeline's own exponential backoff between them
        /// (<see cref="RetryBackoffAllowance"/> covers the standard 2s/4s-with-jitter schedule).
        /// </summary>
        public TimeSpan TotalRequestTimeout =>
            AttemptTimeout * (MaxRetryAttempts + 1) + RetryBackoffAllowance;

        /// <summary>Slack for the resilience pipeline's exponential, jittered retry delays.</summary>
        public static TimeSpan RetryBackoffAllowance => TimeSpan.FromSeconds(30);

        /// <summary>
        /// The circuit breaker's sampling window. The standard resilience options validator refuses
        /// a window shorter than twice <see cref="AttemptTimeout"/>, so this tracks the derived
        /// attempt timeout rather than the library's fixed 30s default.
        /// </summary>
        public TimeSpan CircuitBreakerSamplingDuration =>
            TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, AttemptTimeout.Ticks * 2));

        /// <summary>
        /// Hard backstop applied to <see cref="HttpClient.Timeout"/>. It sits <i>outside</i> the
        /// resilience pipeline and would abort the whole thing — including the throttle wait — so
        /// it must be strictly larger than <see cref="TotalRequestTimeout"/>, which is the timeout
        /// that is actually meant to bite.
        /// </summary>
        public TimeSpan HttpClientTimeout => TotalRequestTimeout + TimeSpan.FromSeconds(30);

        /// <summary>
        /// Unit cost in USD per <c>endpointTemplate</c> (the same low-cardinality templates stored on
        /// <see cref="FlightAssistant.Domain.Configuration.Entities.AeroApiCall.EndpointTemplate"/>),
        /// bound from <c>AeroApi:Pricing</c>. Deliberately not hardcoded in C# — FlightAware changes
        /// prices. An endpoint missing from this dictionary costs 0 rather than throwing, so a missing
        /// price shows up as a visible $0 line in the usage rollup instead of failing it.
        /// </summary>
        public Dictionary<string, decimal> Pricing { get; set; } = new();

        // IAeroApiUsagePricingOptions.Pricing is IReadOnlyDictionary — a property's declared type
        // can't covary implicitly, so this is an explicit interface implementation over the same
        // dictionary rather than a second, divergent copy.
        IReadOnlyDictionary<string, decimal> IAeroApiUsagePricingOptions.Pricing => Pricing;

        /// <summary>Informational budget ceiling shown against actual metered spend; enforces nothing on its own.</summary>
        // The double-typed Range overload is used deliberately, not the typeof(decimal) string-pair
        // overload: RangeAttribute parses that string pair with the current culture, and "0.01"
        // fails to parse (and so fails validation for every legal value) under a non-"."-decimal
        // culture. The double overload compares by converting the bound value to double instead.
        [Range(0.01, 1_000_000)]
        public decimal MonthlyBudgetUsd { get; set; } = 5m;

        /// <summary>
        /// How long <see cref="FlightAssistant.Domain.Configuration.Entities.AeroApiCall"/> rows are
        /// kept before cleanup.
        /// </summary>
        [Range(1, 3650)]
        public int UsageRetentionDays { get; set; } = 90;
    }
}
