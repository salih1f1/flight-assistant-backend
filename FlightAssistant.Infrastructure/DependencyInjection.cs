using System.Net;
using System.Net.Http.Headers;
using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.Notifications;
using FlightAssistant.Application.Interfaces.DataSources;
using FlightAssistant.Application.Interfaces.Jobs;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.ProviderBudget;
using FlightAssistant.Infrastructure.ExternalServices;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi.Caching;
using FlightAssistant.Infrastructure.DataSources;
using FlightAssistant.Infrastructure.DataSources.Probes;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using FlightAssistant.Infrastructure.ExternalServices.GitHub;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using FlightAssistant.Infrastructure.Jobs;
using FlightAssistant.Infrastructure.Notifications;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
namespace FlightAssistant.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, bool isDevelopment = false)
        {
            // EF interceptors
            services.AddScoped<DomainEventDispatchInterceptor>();
            services.AddScoped<AdminAuditInterceptor>();

            // Repositories
            services.AddScoped<ITravelerRepository, TravelerRepository>();
            services.AddScoped<IFlightRepository, FlightRepository>();
            services.AddScoped<ILoungeRepository, LoungeRepository>();
            services.AddScoped<ILoyaltyRepository, LoyaltyRepository>();
            services.AddScoped<IRefDataRepository, RefDataRepository>();

            // Admin repositories
            services.AddScoped<IAdminTravelerRepository, AdminTravelerRepository>();
            services.AddScoped<IAdminFlightRepository, AdminFlightRepository>();
            services.AddScoped<IAdminAuditRepository, AdminAuditRepository>();
            services.AddScoped<IAdminStatsRepository, AdminStatsRepository>();
            services.AddScoped<IBrowsableDataRepository, BrowsableDataRepository>();
            services.AddScoped<IAirlineRepository, AirlineRepository>();
            services.AddScoped<IAirportRepository, AirportRepository>();
            services.AddScoped<ICountryRepository, CountryRepository>();
            services.AddScoped<IAllianceRepository, AllianceRepository>();
            services.AddScoped(typeof(IVerifiableRepository<>), typeof(EfVerifiableRepository<>));

            // Verification queue (#367): one repository behind the queue and the job's interface.
            services.AddScoped<IVerificationQueue, VerificationRequestRepository>();
            services.AddScoped<IVerificationRequestRepository, VerificationRequestRepository>();

            // Needs-attention counts (#385, Task 2): reads Airport/Airline/Country directly, so it is
            // its own port rather than living on IRefDataRepository or IVerificationRequestRepository.
            services.AddScoped<IVerificationNeedsAttentionRepository, VerificationNeedsAttentionRepository>();

            // Data-source health (#460): the status table behind the /health page, the catalog of
            // what is watched, and the in-memory observer every seam records into. The observer is
            // registered under its own type and under IDataSourceObserver as the SAME instance —
            // the pattern AeroApiThrottleGate uses — since a second copy would buffer into nothing.
            services.AddScoped<IDataSourceStatusRepository, DataSourceStatusRepository>();
            services.AddSingleton<IDataSourceCatalog, DataSourceCatalog>();
            services.AddSingleton<BufferedDataSourceObserver>();
            services.AddSingleton<IDataSourceObserver>(sp => sp.GetRequiredService<BufferedDataSourceObserver>());

            // The "Check now" probes, one per source that supports one. Scoped, because the
            // Postgres probe needs the request's DbContext; the command resolves them all and
            // picks by key. The App Insights probe is registered in the API layer instead, beside
            // the connection-string resolver it has to agree with (#440).
            services.AddScoped<IDataSourceProbe, PostgresProbe>();
            services.AddScoped<IDataSourceProbe, RedisProbe>();
            services.AddScoped<IDataSourceProbe, ApnsProbe>();
            services.AddScoped<IDataSourceProbe, AeroApiProbe>();
            services.AddScoped<IDataSourceProbe, GitHubProbe>();
            services.AddScoped<IDataSourceProbe, OurAirportsProbe>();
            services.AddScoped<IDataSourceProbe, MledozeProbe>();
            services.AddScoped<IDataSourceProbe, AeroDataBoxProbe>();

            // Verification sources (#367 task 3): the two free outside datasets an automated check
            // can name. Both hold no credentials — OurAirports and mledoze's raw GitHub file are
            // public, unauthenticated URLs — so, unlike the GitHub clients below, following a
            // redirect is not a credential-leak risk and the default SocketsHttpHandler (which
            // follows redirects) is left as-is deliberately.
            //
            // Registered as singletons, not scoped: each source's parsed dataset is reused for up to
            // 24h (spec §4.3), and the job's cycle runs in a fresh, short-lived DI scope every time —
            // a scoped source would re-download on every single cycle instead of persisting across
            // them. Each source is handed IHttpClientFactory itself (fix round 1, item 5), not a
            // pre-created HttpClient captured once at registration time: it calls CreateClient(name)
            // fresh for every download, so this singleton never outlives one HttpMessageHandler
            // instance across the app's whole lifetime the way holding a single created HttpClient
            // forever would. VerificationSourceRegistry is a singleton for the same cache-persistence
            // reason, and depends on nothing but the sources themselves (IEnumerable<IVerificationSource>,
            // resolved from every AddSingleton<IVerificationSource> registration below) — no
            // repository/EF dependency (controller ruling); Task 4's job cycle is what asks
            // IVerificationRequestRepository which sources are still in use.
            //
            // client.Timeout is a looser backstop only (same layering CLAUDE.md documents for
            // AeroApi): VerificationSourceDownloader's own linked CancellationTokenSource, cancelled
            // after each source's DownloadTimeout, is what actually bounds send *and* parse (fix
            // round 1, item 1) — HttpClient.Timeout alone would cover only time-to-headers, since
            // ResponseHeadersRead makes HttpClient drop its own timeout the instant headers arrive.
            services.AddHttpClient(OurAirportsVerificationSource.HttpClientName, client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("FlightAssistant-VerificationJob");
                client.Timeout = OurAirportsVerificationSource.DownloadTimeout + TimeSpan.FromSeconds(30);
            })
                .AddDataSourceObservation(DataSourceKeys.OurAirports);

            services.AddHttpClient(MledozeCountriesVerificationSource.HttpClientName, client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("FlightAssistant-VerificationJob");
                client.Timeout = MledozeCountriesVerificationSource.DownloadTimeout + TimeSpan.FromSeconds(30);
            })
                .AddDataSourceObservation(DataSourceKeys.Mledoze);

            services.AddSingleton(sp => new OurAirportsVerificationSource(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<OurAirportsVerificationSource>>()));
            services.AddSingleton<IVerificationSource>(sp => sp.GetRequiredService<OurAirportsVerificationSource>());

            services.AddSingleton(sp => new MledozeCountriesVerificationSource(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<MledozeCountriesVerificationSource>>()));
            services.AddSingleton<IVerificationSource>(sp => sp.GetRequiredService<MledozeCountriesVerificationSource>());

            // aeroapi (#401): airports and airlines, metered, against AeroAPI itself rather than a
            // downloaded dataset. Registered a singleton for the same reason as the two sources
            // above (IVerificationSourceRegistry resolves IEnumerable<IVerificationSource> once, at
            // its own singleton construction), even though this source holds no dataset to persist —
            // it still takes IHttpClientFactory, not a captured HttpClient, calling CreateClient
            // fresh per lookup. It reuses the shared "AeroApi" named client registered below (auth,
            // resilience, AeroApiThrottleGate) rather than a dedicated client of its own, so a
            // verification lookup is throttled exactly like every other AeroAPI call.
            services.AddSingleton(sp => new AeroApiVerificationSource(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ICacheService>(),
                sp.GetRequiredService<IOptions<AeroApiOptions>>(),
                sp.GetRequiredService<ILogger<AeroApiVerificationSource>>()));
            services.AddSingleton<IVerificationSource>(sp => sp.GetRequiredService<AeroApiVerificationSource>());

            services.AddSingleton<IVerificationSourceRegistry, VerificationSourceRegistry>();

            // AeroAPI — the sole flight data provider (issue #280). This wires the HTTP
            // foundation: options, error mapping (AeroApiResponse), and the shared outbound
            // throttle.
            services.AddOptions<AeroApiOptions>()
                .BindConfiguration("AeroApi")
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // SearchFlightsQueryValidator (Application layer) needs the configured search horizon
            // to bound requests against, but Application must never reference Infrastructure —
            // AeroApiOptions implements IFlightSearchHorizonOptions (defined in Application) so
            // this is a plain translation, not a leak of AeroApiOptions itself.
            services.AddScoped<IFlightSearchHorizonOptions>(sp =>
                sp.GetRequiredService<IOptions<AeroApiOptions>>().Value);

            // Same translation, same reason (issue #300): GetAeroApiUsageQueryHandler renders the
            // spend gauge against the configured monthly budget, which lives on AeroApiOptions.
            services.AddScoped<IAeroApiBudgetOptions>(sp =>
                sp.GetRequiredService<IOptions<AeroApiOptions>>().Value);

            // Same translation (#356): FlightSyncPlanner starts Live polling StatusWindowFutureDays
            // before departure, the furthest ahead GET /flights/{ident} can answer.
            services.AddScoped<IFlightStatusWindowOptions>(sp =>
                sp.GetRequiredService<IOptions<AeroApiOptions>>().Value);

            // AeroApiThrottleGate's constructor requires a TimeProvider, and nothing else in this
            // solution (nor AddStandardResilienceHandler, nor the generic host) registers one —
            // TryAddSingleton so a real caller elsewhere in the app can still supply its own without
            // a duplicate-registration conflict.
            services.TryAddSingleton(TimeProvider.System);

            // Singleton so every AeroAPI HttpClient contends on the same gate — the personal tier's
            // ~5 queries/minute limit is a process-wide budget, not a per-client one.
            services.AddSingleton<AeroApiThrottleGate>();
            services.AddTransient<AeroApiThrottlingHandler>();

            // Same translation pattern as IAeroApiBudgetOptions above, for the usage query's
            // NextSlotUtc (issue #300): AeroApiThrottleGate implements IAeroApiThrottleObserver
            // (defined in Application) itself, and is resolved here under that interface as the
            // very same singleton instance — not a second gate.
            services.AddSingleton<IAeroApiThrottleObserver>(sp =>
                sp.GetRequiredService<AeroApiThrottleGate>());

            var aeroApiClientBuilder = services.AddHttpClient(AeroApiVerificationSource.HttpClientName, (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<AeroApiOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseUrl);
                client.DefaultRequestHeaders.Add("x-apikey", options.ApiKey);
                // Deliberately NOT TimeoutSeconds: HttpClient.Timeout wraps the entire resilience
                // pipeline, throttle wait included, so it must be looser than the pipeline's own
                // TotalRequestTimeout or it would abort the very waits the pipeline is sized for.
                client.Timeout = options.HttpClientTimeout;
            });

            // Resilience is registered first (= outermost) so that every retry attempt re-enters the
            // throttling handler below and waits its turn too — not just the first attempt of a call.
            // AddStandardResilienceHandler returns a pipeline-specific builder, not IHttpClientBuilder,
            // so the throttle handler is added as a separate statement on the original builder rather
            // than chained off it.
            //
            // Because the throttle sits INSIDE this pipeline, a request can spend up to a whole
            // MinRequestInterval queued before it ever reaches the transport — and that wait happens
            // inside AttemptTimeout. The library's 10s default is shorter than the 12s default
            // interval, which meant every second call in an interval window was killed before it
            // sent anything, retried twice (a timeout counts as transient), and then failed the 30s
            // total timeout: a multi-page search failed from page 2 on. Every timeout below is
            // therefore derived from AeroApiOptions rather than left at a default or hardcoded, so
            // tuning the interval cannot silently reintroduce it.
            aeroApiClientBuilder.AddStandardResilienceHandler().Configure((options, sp) =>
            {
                var aeroApi = sp.GetRequiredService<IOptions<AeroApiOptions>>().Value;

                options.AttemptTimeout.Timeout = aeroApi.AttemptTimeout;
                options.TotalRequestTimeout.Timeout = aeroApi.TotalRequestTimeout;
                options.CircuitBreaker.SamplingDuration = aeroApi.CircuitBreakerSamplingDuration;

                options.Retry.MaxRetryAttempts = aeroApi.MaxRetryAttempts;
                // 429 must never be retried automatically: this is a $5/month metered tier, and
                // retrying a rate-limit response only makes the throttling worse. A request marked
                // with AeroApiRetry.Skip isn't retried either — a scheduled status poll, whose next
                // due sync is its retry (#375). The request is read from the resilience context,
                // because an exception outcome (a network error or an attempt timeout) has no
                // response to find it on. All other transient outcomes (5xx, timeouts, network
                // errors) keep the default handling.
                options.Retry.ShouldHandle = args =>
                {
                    if (args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests)
                        return ValueTask.FromResult(false);

                    if (AeroApiRetry.IsSkipped(args.Context.GetRequestMessage()))
                        return ValueTask.FromResult(false);

                    return ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome));
                };
            });

            aeroApiClientBuilder.AddHttpMessageHandler<AeroApiThrottlingHandler>();
            aeroApiClientBuilder.AddDataSourceObservation(DataSourceKeys.AeroApi);

            services.AddScoped(sp => new AeroApiAirportTimezoneLookup(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(AeroApiVerificationSource.HttpClientName),
                sp.GetRequiredService<ICacheService>(),
                sp.GetRequiredService<IOptions<AeroApiOptions>>(),
                sp.GetRequiredService<ILogger<AeroApiAirportTimezoneLookup>>()));

            services.AddScoped(sp => new AeroApiFlightSearchProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(AeroApiVerificationSource.HttpClientName),
                sp.GetRequiredService<AeroApiAirportTimezoneLookup>(),
                sp.GetRequiredService<IOptions<AeroApiOptions>>(),
                sp.GetRequiredService<ILogger<AeroApiFlightSearchProvider>>()));

            // AeroDataBox (#422): route searches (both airports, no usable flight number) move here
            // from AeroAPI's per-search-billed /schedules call, onto AeroDataBox's free tier.
            // ApiKey is deliberately NOT [Required], unlike AeroApiOptions — the app must still
            // start without it (GitHubOptions.IsConfigured precedent), and FlightSearchProviderSelector
            // below falls back to AeroAPI when it is unset. Every OTHER property (TimeoutSeconds) IS
            // validated on start (#422 final review, finding 3): an invalid value used to throw only
            // when a search first resolved the client — via the eagerly-constructed provider below —
            // taking down every GET /flights/search request, not just route searches.
            services.AddOptions<AeroDataBoxOptions>()
                .BindConfiguration("AeroDataBox")
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // No AeroApiThrottleGate and no resilience handler on this client: the gate exists to
            // stay inside a METERED tier's rate limit, and every resilience timeout in this repo is
            // derived from AeroApi:MinRequestInterval — neither applies to a free, quota-based API.
            // TimeoutSeconds -> client.Timeout is this client's ONLY timeout as a result. The key is
            // sent only when configured, never as a null/empty header value.
            //
            // The primary handler disables redirects (#422 final review, finding 2 — the GitHub
            // clients' own precedent below): the default SocketsHttpHandler follows a 3xx, and .NET
            // strips only the Authorization header on redirect, not a custom one, so X-RapidAPI-Key
            // would otherwise be replayed to whatever host the redirect names.
            services.AddHttpClient(AeroDataBoxFlightSearchProvider.HttpClientName, (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<AeroDataBoxOptions>>().Value;
                client.BaseAddress = new Uri(AeroDataBoxOptions.ApiBaseUrl);
                if (options.IsConfigured)
                    client.DefaultRequestHeaders.Add("X-RapidAPI-Key", options.ApiKey);
                client.DefaultRequestHeaders.Add("X-RapidAPI-Host", AeroDataBoxOptions.RapidApiHost);
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .AddDataSourceObservation(DataSourceKeys.AeroDataBox);

            services.AddScoped(sp => new AeroDataBoxFlightSearchProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(AeroDataBoxFlightSearchProvider.HttpClientName),
                sp.GetRequiredService<IOptions<AeroDataBoxOptions>>(),
                sp.GetRequiredService<ILogger<AeroDataBoxFlightSearchProvider>>()));

            // IFlightSearchProvider resolves to the caching decorator, which now fronts BOTH
            // providers (issue #282, widened by #422) — GET /flights/search is anonymous and only
            // rate-limited per IP, so without a cache in front of it, a scripted client could drain
            // the $5/month metered AeroAPI tier long before the throttle (a latency control, not a
            // spend cap) even slowed it down. AeroDataBox costs nothing, but a repeated identical
            // search still shouldn't re-issue two upstream calls on every request.
            //
            // FlightSearchProviderSelector sits INSIDE the cache and OUTSIDE the AeroAPI budget
            // decorator: the budget decorator refuses on AeroAPI spend specifically, so wrapping the
            // free AeroDataBox path in it would refuse free searches for no reason, and the cache
            // must stay outermost so a cached result is still answered past the budget (#303). A
            // consequence worth stating rather than discovering later: a route search is no longer
            // refused once the AeroAPI budget leaves Normal (AeroApiFlightSearchBudgetDecorator
            // refuses from SoftLimit onward, not only Exhausted), because it never touches AeroAPI
            // once AeroDataBox is configured — an improvement, and a real behaviour change.
            //
            // The cache key (AeroApiFlightSearchCachingDecorator.BuildCacheKey) has no provider
            // dimension. #422 final review, finding 4: that is NOT safe merely because routing is
            // deterministic by criteria shape — it also depends on whether AeroDataBox:ApiKey is
            // configured (AeroDataBoxOptions.IsConfigured), which is fixed for a process's lifetime
            // but not across a restart or an instance sharing Redis. After the key is added or
            // removed, a route-search key can be served the OTHER provider's cached rows until the
            // TTL runs out (AeroApiOptions.SearchCacheTtlPastOrToday 5 min / SearchCacheTtlFuture
            // 12h). On a rollback — pulling the key because AeroDataBox rows turned out wrong or
            // empty — flush the "aeroapi:flight-search:" cache prefix rather than waiting out the
            // TTL. If a shape is ever routed to both providers AT ONCE (not merely at different
            // times), the key must gain a provider dimension.
            //
            // AeroApiFlightSearchProvider itself stays registered and resolvable — the decorator
            // wraps it, it does not replace it.
            services.AddScoped<IFlightSearchProvider>(sp => new AeroApiFlightSearchCachingDecorator(
                new FlightSearchProviderSelector(
                    sp.GetRequiredService<AeroDataBoxFlightSearchProvider>(),
                    // Inside the cache, so a cached search is answered past the budget (#303).
                    new AeroApiFlightSearchBudgetDecorator(
                        sp.GetRequiredService<AeroApiFlightSearchProvider>(),
                        sp.GetRequiredService<IProviderBudgetGuard>(),
                        sp.GetRequiredService<ILogger<AeroApiFlightSearchBudgetDecorator>>()),
                    sp.GetRequiredService<IOptions<AeroDataBoxOptions>>(),
                    sp.GetRequiredService<ILogger<FlightSearchProviderSelector>>()),
                sp.GetRequiredService<ICacheService>(),
                sp.GetRequiredService<IOptions<AeroApiOptions>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<AeroApiFlightSearchCachingDecorator>>()));

            // Flight status (issue #280). ProviderName ("AeroAPI") feeds the stored
            // FlightStatusEvent dedup hash (see SyncFlightStatusCommand.cs) — the prior provider's
            // stored events keep hashes computed under its own name, so each tracked flight may
            // emit one duplicate status event the first time it is synced after this deploy.
            // Accepted, not migrated; see AeroApiFlightStatusProviderTests for the documenting test.
            services.AddScoped(sp => new AeroApiFlightStatusProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(AeroApiVerificationSource.HttpClientName),
                sp.GetRequiredService<IOptions<AeroApiOptions>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<AeroApiFlightStatusProvider>>()));

            services.AddScoped<IFlightStatusProvider>(sp =>
                sp.GetRequiredService<AeroApiFlightStatusProvider>());

            // Account usage (issue #300) — same named client as search and status, so the spend
            // report is throttled, retried and authenticated identically. The endpoint it proxies
            // is itself billed at $0.00.
            services.AddScoped(sp => new AeroApiAccountUsageProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(AeroApiVerificationSource.HttpClientName),
                sp.GetRequiredService<IOptions<AeroApiOptions>>(),
                sp.GetRequiredService<ILogger<AeroApiAccountUsageProvider>>()));

            services.AddScoped<IAeroApiAccountUsageProvider>(sp =>
                sp.GetRequiredService<AeroApiAccountUsageProvider>());

            // The budget guard (#303) reads this month's spend from the same cached usage report the
            // admin gauge uses. Flight search and status sync ask it before a call they can hold back.
            services.AddScoped<IProviderBudgetGuard, ProviderBudgetGuard>();

            // Shared by the status sync and, from Phase 2 of #364, the alert webhook (#386). Scoped: it
            // writes through the request's own IFlightRepository and never saves.
            services.AddScoped<IFlightStatusApplier, FlightStatusApplier>();

            // CI results (issue #332): the admin tests page reads the CI workflow's runs and its
            // test-results artifact through the API, so the panel never holds a GitHub token.
            // GitHub:Token is optional; without it the CI endpoints answer 503 and the app still starts.
            services.AddOptions<GitHubOptions>()
                .BindConfiguration("GitHub")
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // The token client never follows redirects. An artifact download 302s to signed blob
            // storage, which GitHubCiRunProvider fetches with the download client below, and that
            // client carries no credentials.
            services.AddHttpClient(GitHubCiRunProvider.ApiClientName, (sp, client) =>
                {
                    var options = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;
                    client.BaseAddress = new Uri(GitHubOptions.ApiBaseUrl);
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("FlightAssistant-API");
                    if (options.IsConfigured)
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
                .AddDataSourceObservation(DataSourceKeys.GitHub);

            services.AddHttpClient(GitHubCiRunProvider.DownloadClientName, client =>
                    client.Timeout = TimeSpan.FromSeconds(60))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
                .AddDataSourceObservation($"{DataSourceKeys.GitHub}-download");

            services.AddScoped<ICiRunProvider>(sp =>
            {
                var clients = sp.GetRequiredService<IHttpClientFactory>();
                return new GitHubCiRunProvider(
                    clients.CreateClient(GitHubCiRunProvider.ApiClientName),
                    clients.CreateClient(GitHubCiRunProvider.DownloadClientName),
                    sp.GetRequiredService<IOptions<GitHubOptions>>(),
                    sp.GetRequiredService<ILogger<GitHubCiRunProvider>>());
            });

            // Push notifications — singleton so the APNs client is created once and reused
            services.AddOptions<ApnsOptions>()
                .BindConfiguration("Apns")
                .Validate(
                    opts => isDevelopment ||
                            (!string.IsNullOrWhiteSpace(opts.BundleId) &&
                             !string.IsNullOrWhiteSpace(opts.KeyId) &&
                             !string.IsNullOrWhiteSpace(opts.TeamId) &&
                             !string.IsNullOrWhiteSpace(opts.P8KeyContent)),
                    "APNs credentials (BundleId, KeyId, TeamId, P8KeyContent) are required in non-Development environments.")
                .ValidateOnStart();

            services.AddSingleton<IPushNotificationService, ApplePushNotificationService>();

            // Job switches (#471): singleton so IsEnabledAsync's per-job cache survives across
            // ticks; it resolves its own DbContext scope per read (CachedJobSwitch), the same
            // shape DataSourceFlushService uses for the same singleton/scoped mismatch.
            services.AddSingleton<IJobSwitch, CachedJobSwitch>();

            // The write side behind PUT /admin/jobs/{job} (Task 8): scoped, like every other
            // repository above, since ToggleJobCommandHandler's write must land in the same request's
            // DbContext/transaction, not a singleton's own long-lived scope.
            services.AddScoped<IJobSwitchRepository, JobSwitchRepository>();

            return services;
        }
    }
}
