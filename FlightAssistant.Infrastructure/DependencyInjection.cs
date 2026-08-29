using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Caching;
using FlightAssistant.Application.Interfaces.Notifications;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Infrastructure.ExternalServices.AviationStack;
using FlightAssistant.Infrastructure.ExternalServices.FlightSearch;
using FlightAssistant.Infrastructure.ExternalServices.FlightStatus;
using FlightAssistant.Infrastructure.Notifications;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

            // Flight status — AviationStack is the sole provider. The IFlightStatusProvider
            // abstraction is kept so a fallback composite can be reintroduced for production.
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

            // Resolve via the typed client so the configured HttpClient (BaseAddress, timeout,
            // resilience handler) is used. Re-activating the concrete type directly would inject
            // a default, unconfigured HttpClient with no BaseAddress.
            services.AddScoped<IFlightStatusProvider>(sp =>
                sp.GetRequiredService<AviationStackFlightStatusProvider>());

            // Flight search — AviationStack splits its data across two endpoints: /v1/flights holds
            // past and present, /v1/flightsFuture holds future schedules, with no overlap.
            // DateRoutingFlightSearchProvider picks between them; nothing above this layer knows.
            foreach (var clientType in new[]
                     {
                         typeof(AviationStackLiveSearchProvider),
                         typeof(AviationStackScheduleSearchProvider),
                         typeof(AviationStackRouteResolver),
                         typeof(AviationStackAirportTimezoneLookup),
                         typeof(OperatingCarrierResolver)
                     })
            {
                services.AddHttpClient(clientType.Name, client =>
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
            }

            services.AddScoped(sp => new AviationStackRouteResolver(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AviationStackRouteResolver)),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<AviationStackRouteResolver>>()));

            services.AddScoped(sp => new AviationStackAirportTimezoneLookup(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AviationStackAirportTimezoneLookup)),
                sp.GetRequiredService<ICacheService>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<AviationStackAirportTimezoneLookup>>()));

            services.AddScoped(sp => new OperatingCarrierResolver(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OperatingCarrierResolver)),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<OperatingCarrierResolver>>()));

            services.AddScoped(sp => new AviationStackLiveSearchProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AviationStackLiveSearchProvider)),
                sp.GetRequiredService<AviationStackAirportTimezoneLookup>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<AviationStackLiveSearchProvider>>()));

            services.AddScoped(sp => new AviationStackScheduleSearchProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AviationStackScheduleSearchProvider)),
                sp.GetRequiredService<AviationStackRouteResolver>(),
                sp.GetRequiredService<OperatingCarrierResolver>(),
                sp.GetRequiredService<AviationStackAirportTimezoneLookup>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<AviationStackScheduleSearchProvider>>()));

            services.AddScoped<IFlightSearchProvider>(sp => new DateRoutingFlightSearchProvider(
                sp.GetRequiredService<AviationStackLiveSearchProvider>(),
                sp.GetRequiredService<AviationStackScheduleSearchProvider>(),
                sp.GetRequiredService<ILogger<DateRoutingFlightSearchProvider>>()));

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
