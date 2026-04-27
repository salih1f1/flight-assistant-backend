using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Notifications;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Infrastructure.ExternalServices.FlightStatus;
using FlightAssistant.Infrastructure.ExternalServices.Visa;
using FlightAssistant.Infrastructure.Notifications;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
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

            // Flight status providers — named HttpClients with base addresses from config
            services.AddHttpClient<AeroDataBoxFlightStatusProvider>(client =>
            {
                var baseUrl = config["AeroDataBox:BaseUrl"] ?? "https://aerodatabox.p.rapidapi.com/";
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
                var apiKey = config["AeroDataBox:ApiKey"];
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    client.DefaultRequestHeaders.Add("X-RapidAPI-Key", apiKey);
                    client.DefaultRequestHeaders.Add("X-RapidAPI-Host", "aerodatabox.p.rapidapi.com");
                }
            }).AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.BackoffType = DelayBackoffType.Exponential;
            });

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

            services.AddScoped<IFlightStatusProvider, CompositeFlightStatusProvider>();

            // Visa requirements provider — Sherpa°
            services.AddHttpClient<SherpaVisaRequirementsProvider>(client =>
            {
                client.BaseAddress = new Uri(config["Sherpa:BaseUrl"] ?? "https://requirements-api.joinsherpa.com/v2/");
                client.Timeout = TimeSpan.FromSeconds(30);
            }).AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.BackoffType = DelayBackoffType.Exponential;
            });
            services.AddScoped<IVisaRequirementsProvider, SherpaVisaRequirementsProvider>();

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
