using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Notifications;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Infrastructure.ExternalServices.FlightStatus;
using FlightAssistant.Infrastructure.ExternalServices.Visa;
using FlightAssistant.Infrastructure.Notifications;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace FlightAssistant.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, bool isDevelopment = false)
        {
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
                var apiKey = config["AeroDataBox:ApiKey"];
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    client.DefaultRequestHeaders.Add("X-RapidAPI-Key", apiKey);
                    client.DefaultRequestHeaders.Add("X-RapidAPI-Host", "aerodatabox.p.rapidapi.com");
                }
            });

            services.AddHttpClient<AviationStackFlightStatusProvider>(client =>
            {
                client.BaseAddress = new Uri(config["AviationStack:BaseUrl"] ?? "http://api.aviationstack.com/v1/");
            });

            services.AddScoped<IFlightStatusProvider, CompositeFlightStatusProvider>();

            // Visa requirements provider — Sherpa°
            services.AddHttpClient<SherpaVisaRequirementsProvider>(client =>
            {
                client.BaseAddress = new Uri(config["Sherpa:BaseUrl"] ?? "https://requirements-api.joinsherpa.com/v2/");
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
