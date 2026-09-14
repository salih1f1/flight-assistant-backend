using System.Net;
using System.Net.Http.Headers;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.Notifications;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi.Caching;
using FlightAssistant.Infrastructure.ExternalServices.GitHub;
using FlightAssistant.Infrastructure.Notifications;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

            var aeroApiClientBuilder = services.AddHttpClient("AeroApi", (sp, client) =>
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
                // retrying a rate-limit response only makes the throttling worse. All other
                // transient outcomes (5xx, timeouts, network errors) keep the default handling.
                options.Retry.ShouldHandle = args =>
                {
                    if (args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests)
                        return ValueTask.FromResult(false);

                    return ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome));
                };
            });

            aeroApiClientBuilder.AddHttpMessageHandler<AeroApiThrottlingHandler>();

            services.AddScoped(sp => new AeroApiAirportTimezoneLookup(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi"),
                sp.GetRequiredService<ICacheService>(),
                sp.GetRequiredService<IOptions<AeroApiOptions>>(),
                sp.GetRequiredService<ILogger<AeroApiAirportTimezoneLookup>>()));

            services.AddScoped(sp => new AeroApiFlightSearchProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi"),
                sp.GetRequiredService<AeroApiAirportTimezoneLookup>(),
                sp.GetRequiredService<IOptions<AeroApiOptions>>(),
                sp.GetRequiredService<ILogger<AeroApiFlightSearchProvider>>()));

            // IFlightSearchProvider resolves to the caching decorator, not AeroApiFlightSearchProvider
            // directly (issue #282) — GET /flights/search is anonymous and only rate-limited per IP,
            // so without a cache in front of it, a scripted client could drain the $5/month metered
            // tier long before the throttle (a latency control, not a spend cap) even slowed it down.
            // AeroApiFlightSearchProvider itself stays registered and resolvable — the decorator wraps
            // it, it does not replace it.
            services.AddScoped<IFlightSearchProvider>(sp => new AeroApiFlightSearchCachingDecorator(
                sp.GetRequiredService<AeroApiFlightSearchProvider>(),
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
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi"),
                sp.GetRequiredService<IOptions<AeroApiOptions>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<AeroApiFlightStatusProvider>>()));

            services.AddScoped<IFlightStatusProvider>(sp =>
                sp.GetRequiredService<AeroApiFlightStatusProvider>());

            // Account usage (issue #300) — same named client as search and status, so the spend
            // report is throttled, retried and authenticated identically. The endpoint it proxies
            // is itself billed at $0.00.
            services.AddScoped(sp => new AeroApiAccountUsageProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi"),
                sp.GetRequiredService<IOptions<AeroApiOptions>>(),
                sp.GetRequiredService<ILogger<AeroApiAccountUsageProvider>>()));

            services.AddScoped<IAeroApiAccountUsageProvider>(sp =>
                sp.GetRequiredService<AeroApiAccountUsageProvider>());

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
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

            services.AddHttpClient(GitHubCiRunProvider.DownloadClientName, client =>
                    client.Timeout = TimeSpan.FromSeconds(60))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

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

            return services;
        }
    }
}
