using FlightAssistant.API.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.Tests.Infrastructure;

/// <summary>
/// What <c>AddAppHealthChecks</c> actually registers (#467). Registering health checks inline in
/// <c>Program.cs</c> left nothing a test could hold, which is how a check that called a 401
/// "Healthy" survived the very issue written to fix that reading.
/// </summary>
public class HealthCheckRegistrationTests
{
    private static IReadOnlyList<HealthCheckRegistration> Registrations(params KeyValuePair<string, string?>[] settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddAppHealthChecks(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.ToList();
    }

    [Fact]
    public void RegistersTheDatabaseAndApnsChecks()
    {
        var names = Registrations().Select(r => r.Name).ToList();

        Assert.Contains("database", names);
        Assert.Contains("apns", names);
    }

    /// <summary>
    /// #467: there is no aeroapi check any more. It reported every HTTP response as Healthy — a 401
    /// included — while the aeroapi data-source row beside it read Unhealthy for the same key. The
    /// row is the single answer now: observed off real calls, checked on demand by the free
    /// account/usage probe, and derived through one evaluator.
    /// </summary>
    [Fact]
    public void RegistersNoAeroApiCheck()
    {
        Assert.DoesNotContain("aeroapi", Registrations().Select(r => r.Name));
    }

    /// <summary>The external tag is what /health/ready's predicate excludes, so a flaky upstream
    /// cannot make App Service restart the app.</summary>
    [Fact]
    public void TagsApnsExternal()
    {
        var apns = Assert.Single(Registrations(), r => r.Name == "apns");

        Assert.Contains("external", apns.Tags);
    }

    [Fact]
    public void RegistersRedis_OnlyWhenAConnectionStringIsConfigured()
    {
        Assert.DoesNotContain("redis", Registrations().Select(r => r.Name.ToLowerInvariant()));

        var withRedis = Registrations(new KeyValuePair<string, string?>("ConnectionStrings:Redis", "localhost:6379"));

        Assert.Contains("redis", withRedis.Select(r => r.Name.ToLowerInvariant()));
    }
}
