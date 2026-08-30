using System.Net;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.Notifications;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using FlightAssistant.Infrastructure.ExternalServices.AviationStack;
using FlightAssistant.Infrastructure.ExternalServices.FlightStatus;
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

            // Repositories
            services.AddScoped<ITravelerRepository, TravelerRepository>();
            services.AddScoped<IFlightRepository, FlightRepository>();
            services.AddScoped<ILoungeRepository, LoungeRepository>();
            services.AddScoped<ILoyaltyRepository, LoyaltyRepository>();
            services.AddScoped<IRefDataRepository, RefDataRepository>();

            // Admin repositories
            services.AddScoped<IAdminTravelerRepository, AdminTravelerRepository>();
            services.AddScoped<IAdminFlightRepository, AdminFlightRepository>();
            services.AddScoped<IAirlineRepository, AirlineRepository>();
            services.AddScoped<IAirportRepository, AirportRepository>();
            services.AddScoped<ICountryRepository, CountryRepository>();
            services.AddScoped<IAllianceRepository, AllianceRepository>();
            services.AddScoped(typeof(IVerifiableRepository<>), typeof(EfVerifiableRepository<>));

            // Flight status — AviationStack's own typed client stays registered (unused by
            // IFlightStatusProvider below) so its existing tests, which construct it directly,
            // keep passing. Task 5 (#280) deletes both the class and this registration.
            services.AddHttpClient<AviationStackFlightStatusProvider>(client =>
            {
                client.BaseAddress = new Uri(config["AviationStack:BaseUrl"] ?? "https://api.aviationstack.com/v1/");
                client.Timeout = TimeSpan.FromSeconds(30);
            }).AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.BackoffType = DelayBackoffType.Exponential;
            });

            // Flight search — AviationStack's live/future split (DateRoutingFlightSearchProvider,
            // AviationStackLiveSearchProvider, AviationStackScheduleSearchProvider,
            // AviationStackRouteResolver, OperatingCarrierResolver) has been fully replaced by
            // AeroApiFlightSearchProvider below (issue #280 / tasks 2 and 4 — task 4's contract
            // reshape removed the grouped FlightSearchResult shape those classes were built
            // around, so they could no longer compile unchanged and were deleted rather than
            // patched to a shape they no longer fit). AviationStackAirportTimezoneLookup has no
            // remaining consumer but stays registered here; Task 5 removes it along with the rest
            // of the AviationStack/ directory.
            services.AddHttpClient(nameof(AviationStackAirportTimezoneLookup), client =>
            {
                client.BaseAddress = new Uri(config["AviationStack:BaseUrl"] ?? "https://api.aviationstack.com/v1/");
                client.Timeout = TimeSpan.FromSeconds(30);
            }).AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.BackoffType = DelayBackoffType.Exponential;
            });

            services.AddScoped(sp => new AviationStackAirportTimezoneLookup(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AviationStackAirportTimezoneLookup)),
                sp.GetRequiredService<ICacheService>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<AviationStackAirportTimezoneLookup>>()));

            // AeroAPI — replacing AviationStack as the flight data provider (issue #280). This wires
            // the HTTP foundation: options, error mapping (AeroApiResponse), and the shared outbound
            // throttle. IFlightStatusProvider still points at AviationStack — task 3 switches that
            // over; the AviationStack status registration above stays in place until then.
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

            // AeroApiThrottleGate's constructor requires a TimeProvider, and nothing else in this
            // solution (nor AddStandardResilienceHandler, nor the generic host) registers one —
            // TryAddSingleton so a real caller elsewhere in the app can still supply its own without
            // a duplicate-registration conflict.
            services.TryAddSingleton(TimeProvider.System);

            // Singleton so every AeroAPI HttpClient contends on the same gate — the personal tier's
            // ~5 queries/minute limit is a process-wide budget, not a per-client one.
            services.AddSingleton<AeroApiThrottleGate>();
            services.AddTransient<AeroApiThrottlingHandler>();

            var aeroApiClientBuilder = services.AddHttpClient("AeroApi", (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<AeroApiOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseUrl);
                client.DefaultRequestHeaders.Add("x-apikey", options.ApiKey);
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            });

            // Resilience is registered first (= outermost) so that every retry attempt re-enters the
            // throttling handler below and waits its turn too — not just the first attempt of a call.
            // AddStandardResilienceHandler returns a pipeline-specific builder, not IHttpClientBuilder,
            // so the throttle handler is added as a separate statement on the original builder rather
            // than chained off it.
            aeroApiClientBuilder.AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 2;
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

            services.AddScoped<IFlightSearchProvider>(sp =>
                sp.GetRequiredService<AeroApiFlightSearchProvider>());

            // Flight status (issue #280 / task 3) — AeroAPI replaces AviationStack here.
            // ProviderName changes from "AviationStack" to "AeroAPI", which feeds the stored
            // FlightStatusEvent dedup hash (see SyncFlightStatusCommand.cs) — existing stored
            // events keep hashes computed under the old name, so each tracked flight may emit one
            // duplicate status event the first time it is synced after this deploys. Accepted, not
            // migrated; see AeroApiFlightStatusProviderTests for the documenting test.
            services.AddScoped(sp => new AeroApiFlightStatusProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("AeroApi"),
                sp.GetRequiredService<IOptions<AeroApiOptions>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<AeroApiFlightStatusProvider>>()));

            services.AddScoped<IFlightStatusProvider>(sp =>
                sp.GetRequiredService<AeroApiFlightStatusProvider>());

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
