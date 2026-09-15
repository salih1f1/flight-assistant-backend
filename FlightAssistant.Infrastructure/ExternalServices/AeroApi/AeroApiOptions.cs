using System.ComponentModel.DataAnnotations;
using FlightAssistant.Application.Interfaces.ExternalServices;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Configuration for the FlightAware AeroAPI client, bound from the <c>AeroApi</c> section.
    /// Registered with <c>ValidateDataAnnotations().ValidateOnStart()</c> so a missing/invalid
    /// value fails fast at startup rather than surfacing as a confusing runtime error on the
    /// first search. Implements <see cref="IFlightSearchHorizonOptions"/>,
    /// <see cref="IAeroApiBudgetOptions"/> and <see cref="IFlightStatusWindowOptions"/> so the
    /// Application-layer query validator, usage query and <c>FlightSyncPlanner</c> can read the
    /// real configured horizon, budget and status window without this (Infrastructure) project
    /// being referenced from Application — see the DI registration in
    /// <c>FlightAssistant.Infrastructure.DependencyInjection</c>.
    /// </summary>
    public sealed class AeroApiOptions : IFlightSearchHorizonOptions, IAeroApiBudgetOptions, IFlightStatusWindowOptions
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

        /// <summary>
        /// The monthly spend the admin panel draws its usage gauge against — the personal tier this
        /// runs on is $5/month. Purely a local reference line: every actual cost figure comes from
        /// AeroAPI's own <c>/account/usage</c> report, because FlightAware bills per page (not per
        /// call) and bills failed calls too, so nothing here prices anything locally.
        /// </summary>
        [Range(0.01, 10_000)]
        public decimal MonthlyBudgetUsd { get; set; } = 5m;

        [Range(1, 12)]
        public int MaxFutureMonths { get; set; } = 12;

        [Range(1, 3)]
        public int MaxPastMonths { get; set; } = 3;

        /// <summary>
        /// How many days into the past <c>GET /flights/{ident}</c> can be asked to look, via the
        /// explicit <c>start</c> parameter <see cref="AeroApiFlightStatusProvider"/> now sends
        /// (issue #283). Mirrors AeroAPI's own hard limit on that parameter (its public reference
        /// documentation states "max 10 days past"; not exercised against the live endpoint here).
        /// The <c>[Range]</c> ceiling is deliberately that same 10 — a configured value AeroAPI
        /// would reject turns every status sync into a guaranteed 400.
        /// </summary>
        [Range(1, 10)]
        public int StatusWindowPastDays { get; set; } = 10;

        /// <summary>
        /// How many days into the future <c>GET /flights/{ident}</c> can be asked to look, via the
        /// explicit <c>end</c> parameter (see <see cref="StatusWindowPastDays"/> for the <c>start</c>
        /// side and the same caveat about how the limit was confirmed). AeroAPI's documented ceiling
        /// is 2 days future, which is also this property's <c>[Range]</c> ceiling.
        ///
        /// <para>
        /// <c>FlightSyncPlanner</c> reads the same value, through <c>IFlightStatusWindowOptions</c>, as
        /// the moment a tracked flight enters Live sync: this many days before its scheduled
        /// departure, the first moment this endpoint can answer for it (#343, #356).
        /// </para>
        /// </summary>
        [Range(1, 2)]
        public int StatusWindowFutureDays { get; set; } = 2;

        /// <summary>
        /// How long <c>AeroApiFlightSearchCachingDecorator</c> caches a search whose
        /// <c>FlightSearchCriteria.Date</c> is today or in the past. Short relative to
        /// <see cref="SearchCacheTtlFuture"/> because a service date that has already started (or is
        /// starting) can still see actual/estimated updates, unlike a schedule further out.
        /// </summary>
        [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
        public TimeSpan SearchCacheTtlPastOrToday { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How long <c>AeroApiFlightSearchCachingDecorator</c> caches a search whose
        /// <c>FlightSearchCriteria.Date</c> is in the future. Long, because a published future
        /// schedule barely moves — this is the TTL doing the actual budget protection, since most
        /// searches (and every scripted/repeated one worth worrying about) target future dates.
        /// </summary>
        [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
        public TimeSpan SearchCacheTtlFuture { get; set; } = TimeSpan.FromHours(12);

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
    }
}
