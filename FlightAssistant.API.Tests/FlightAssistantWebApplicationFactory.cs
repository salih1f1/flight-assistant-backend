using FlightAssistant.Infrastructure.Persistance;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Headers;

namespace FlightAssistant.API.Tests;

public class FlightAssistantWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Run as Development so APNs validation is skipped and Swagger is enabled
        builder.UseEnvironment("Development");

        // Supply all values required by Cfg() in Program.cs without hitting Azure
        builder.ConfigureAppConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PostgresConnectionString"]    = "Host=localhost;Database=test-placeholder",
                ["Azure:TenantId"]              = "test-tenant-id",
                ["Azure:ClientId"]              = "test-client-id",
                ["Azure:Instance"]              = "https://login.microsoftonline.com/",
                ["Azure:Domain"]                = "test.onmicrosoft.com",
                ["Azure:SignUpSignInPolicyId"]   = "B2C_1_signupsignin",
                ["Azure:Issuer"]                = "https://test.b2clogin.com/test-tenant-id/v2.0/",
                ["Azure:Authority"]             = "https://test.b2clogin.com/test-tenant-id/",
                // Azure:KeyVault intentionally absent — prevents Key Vault access
            });
        });

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
