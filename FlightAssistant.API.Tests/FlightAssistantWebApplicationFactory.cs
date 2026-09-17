using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;

namespace FlightAssistant.API.Tests;

public class FlightAssistantWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // ConfigureAppConfiguration is applied after builder.Build() — too late for Cfg() calls at
        // the top of Program.cs (lines 145–153). Instead, UseEnvironment("Testing") is applied
        // early (before WebApplication.CreateBuilder returns), so appsettings.Testing.json is
        // loaded as a default configuration source before any Cfg() call executes.
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // ── Replace PostgreSQL DbContext with in-memory ──────────────────
            // In EF Core 10, removing DbContextOptions<T> is not sufficient: EF Core
            // internally shares a service provider keyed on all registered options. When
            // both the original Npgsql options and the new InMemory options end up visible
            // to the internal service-provider builder, it throws "Services for database
            // providers … have been registered". Strip everything EF-Core and DbContext
            // related so the internal provider is built from scratch with InMemory only.
            var efCoreAssemblyPrefixes = new[]
            {
                "Microsoft.EntityFrameworkCore",
                "Npgsql.EntityFrameworkCore",
            };
            var efToRemove = services
                .Where(d =>
                {
                    if (d.ServiceType == typeof(FlightAssistantDbContext)) return true;
                    var svcAssembly = d.ServiceType.Assembly.GetName().Name;
                    if (svcAssembly is not null && efCoreAssemblyPrefixes.Any(p => svcAssembly.StartsWith(p)))
                        return true;
                    var implAssembly = d.ImplementationType?.Assembly.GetName().Name;
                    if (implAssembly is not null && efCoreAssemblyPrefixes.Any(p => implAssembly.StartsWith(p)))
                        return true;
                    return false;
                })
                .ToList();
            foreach (var d in efToRemove)
                services.Remove(d);

            services.AddDbContext<FlightAssistantDbContext>(opt =>
                opt.UseInMemoryDatabase("FlightAssistantIntegrationTests"));

            // ── Replace JWT auth with the test scheme ────────────────────────
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });

            // ── Remove all background services (they contact real infra) ─────
            var hosted = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .ToList();
            foreach (var d in hosted)
                services.Remove(d);

            // ── Verification source downloads throw ──────────────────────────
            // The real source registry stays (the admin endpoints list its sources), but no test may
            // download a dataset: anything reaching either source's named client fails loudly instead.
            foreach (var clientName in new[] { OurAirportsVerificationSource.HttpClientName, MledozeCountriesVerificationSource.HttpClientName })
                services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => new VerificationDownloadRefusingHandler());

            // ── Remove health checks that require real DB / Redis / APNs ─────
            services.Configure<HealthCheckServiceOptions>(opts =>
                opts.Registrations.Clear());

            // ── Remove OpenTelemetry providers and their dependents ──────────
            // Azure.Monitor.OpenTelemetry.Exporter 1.8.x throws at startup when no
            // ApplicationInsights connection string is configured. Remove all OTel
            // service registrations so the exporter is never instantiated in tests.
            var otelAssemblyPrefixes = new[]
            {
                "OpenTelemetry",
                "Azure.Monitor.OpenTelemetry",
                "Microsoft.Extensions.Diagnostics.Metrics"
            };
            var otelToRemove = services
                .Where(d =>
                {
                    // Match on service type assembly
                    if (d.ServiceType.Assembly.GetName().Name is { } svcName
                        && otelAssemblyPrefixes.Any(p => svcName.StartsWith(p)))
                        return true;
                    // Also match on implementation type assembly (e.g. OpenTelemetryMetricsListener
                    // registered as IMetricsListener — service type is from M.E.Diagnostics but
                    // implementation type is from OpenTelemetry, causing MeterProvider to be
                    // missing after the rest of OTel is stripped out).
                    if (d.ImplementationType?.Assembly.GetName().Name is { } implName
                        && otelAssemblyPrefixes.Any(p => implName.StartsWith(p)))
                        return true;
                    return false;
                })
                .ToList();
            foreach (var d in otelToRemove)
                services.Remove(d);

            // ── Replace Serilog's ILoggerFactory with standard console logging ─
            // Program.cs uses UseSerilog() which registers a singleton ILoggerFactory
            // that calls ReloadableLogger.Freeze() when the DI container is first
            // built. If the same process runs tests more than once (IDE runners that
            // keep the process alive), the second factory creation fails because the
            // ReloadableLogger is already frozen. Replacing the factory here means
            // Freeze() is never called and CloseAndFlush is never registered, so
            // Serilog's process-wide static state stays clean across restarts.
            var loggerFactoryDescs = services
                .Where(d => d.ServiceType == typeof(ILoggerFactory))
                .ToList();
            foreach (var d in loggerFactoryDescs)
                services.Remove(d);
            services.TryAddSingleton<ILoggerFactory>(new LoggerFactory());

            // ── No test reaches a real flight search ─────────────────────────
            // Same posture as the verification download handler below. Since #444 the daily schedule
            // check calls IFlightSearchProvider, and that runs on the ordinary "traveler syncs a
            // flight weeks out" path — so without this, tests that have nothing to do with searching
            // would attempt a real AeroAPI call and fail slowly on its timeout.
            // Answering no rows means an unstubbed schedule check reports NotInSchedule; a test that
            // cares what the schedule says overrides this registration with its own provider.
            var searchDescs = services
                .Where(d => d.ServiceType == typeof(IFlightSearchProvider))
                .ToList();
            foreach (var d in searchDescs)
                services.Remove(d);
            services.AddScoped<IFlightSearchProvider, NoRowsFlightSearchProvider>();
        });
    }

    /// <summary>Answers every search with no rows, so no test makes a real one. See the note above.</summary>
    private sealed class NoRowsFlightSearchProvider : IFlightSearchProvider
    {
        public Task<IReadOnlyList<FlightSearchResult>> SearchAsync(FlightSearchCriteria criteria, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FlightSearchResult>>([]);
    }

    /// <summary>The message a test gets when anything tries to download a verification dataset.</summary>
    public const string VerificationDownloadRefused = "Tests must not download verification datasets";

    private sealed class VerificationDownloadRefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(VerificationDownloadRefused);
    }

    // Convenience: create a client pre-authenticated as a regular user
    public HttpClient CreateUserClient(string? userId = null)
    {
        userId ??= Guid.NewGuid().ToString();
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", $"test-{userId}");
        return client;
    }

    // Convenience: create a client pre-authenticated as an admin
    public HttpClient CreateAdminClient(string? userId = null)
    {
        userId ??= Guid.NewGuid().ToString();
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", $"test-admin-{userId}");
        return client;
    }
}
