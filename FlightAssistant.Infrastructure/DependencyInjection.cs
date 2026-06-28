using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Notifications;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Infrastructure.ExternalServices.FlightSearch;
using FlightAssistant.Infrastructure.ExternalServices.FlightStatus;
using FlightAssistant.Infrastructure.Notifications;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
                client.BaseAddress = new Uri(config["AviationStack:BaseUrl"] ?? "http://api.aviationstack.com/v1/");
                client.Timeout = TimeSpan.FromSeconds(30);
            }).AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.BackoffType = DelayBackoffType.Exponential;
            });

            services.AddScoped<IFlightStatusProvider, AviationStackFlightStatusProvider>();

            // Flight search — AviationStack filters by both airports server-side in one call.
            services.AddHttpClient<AviationStackFlightSearchProvider>(client =>
            {
                client.BaseAddress = new Uri(config["AviationStack:BaseUrl"] ?? "http://api.aviationstack.com/v1/");
                client.Timeout = TimeSpan.FromSeconds(30);
            }).AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.BackoffType = DelayBackoffType.Exponential;
            });
            services.AddScoped<IFlightSearchProvider, AviationStackFlightSearchProvider>();

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
