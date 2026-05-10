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
            var toRemove = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<FlightAssistantDbContext>)
                         || d.ServiceType == typeof(FlightAssistantDbContext))
                .ToList();
            foreach (var d in toRemove)
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

            // ── Remove health checks that require real DB / Redis / APNs ─────
            services.Configure<HealthCheckServiceOptions>(opts =>
                opts.Registrations.Clear());

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
        });
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
