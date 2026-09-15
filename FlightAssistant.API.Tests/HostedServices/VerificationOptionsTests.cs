using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FlightAssistant.API.Tests.HostedServices;

/// <summary>
/// <see cref="VerificationOptions"/> through the real
/// <see cref="VerificationJobServiceCollectionExtensions.AddVerificationJob"/> registration. Validation
/// is triggered through <see cref="IStartupValidator"/>, which is what <c>ValidateOnStart</c> runs when
/// the host starts, so these prove a bad value stops startup rather than failing later on first use.
/// </summary>
public class VerificationOptionsTests
{
    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddSingleton(TimeProvider.System);
        services.AddVerificationJob();
        return services.BuildServiceProvider();
    }

    private static void ValidateOnStart(IServiceProvider provider) =>
        provider.GetRequiredService<IStartupValidator>().Validate();

    [Fact]
    public void Defaults_BindWithNoConfiguration_AndPassValidationOnStart()
    {
        using var provider = Build(new Dictionary<string, string?>());

        ValidateOnStart(provider);
        var options = provider.GetRequiredService<IOptions<VerificationOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(30), options.PollInterval);
        Assert.Equal(10, options.BatchSize);
        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(180, options.StaleAfterDays);
        Assert.Equal(TimeSpan.FromMinutes(30), options.IdleRelease);
        Assert.Equal(new[] { "ourairports" }, options.DefaultSources.For(VerificationEntityType.Airport));
        Assert.Equal(new[] { "aeroapi" }, options.DefaultSources.For(VerificationEntityType.Airline));
        Assert.Equal(new[] { "mledoze" }, options.DefaultSources.For(VerificationEntityType.Country));

        // Application's view is the same bound instance.
        Assert.Same(options, provider.GetRequiredService<IVerificationJobOptions>());
    }

    [Fact]
    public void ConfiguredValues_Bind()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Verification:Enabled"] = "false",
            ["Verification:PollInterval"] = "00:02:00",
            ["Verification:BatchSize"] = "25",
            ["Verification:MaxAttempts"] = "5",
            ["Verification:StaleAfterDays"] = "30",
            ["Verification:IdleRelease"] = "00:05:00",
            ["Verification:DefaultSources:Airport"] = "ourairports,aeroapi"
        });

        ValidateOnStart(provider);
        var options = provider.GetRequiredService<IOptions<VerificationOptions>>().Value;

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(2), options.PollInterval);
        Assert.Equal(25, options.BatchSize);
        Assert.Equal(5, options.MaxAttempts);
        Assert.Equal(30, options.StaleAfterDays);
        Assert.Equal(TimeSpan.FromMinutes(5), options.IdleRelease);
        Assert.Equal(new[] { "ourairports", "aeroapi" }, options.DefaultSources.For(VerificationEntityType.Airport));
        Assert.Equal(new[] { "mledoze" }, options.DefaultSources.For(VerificationEntityType.Country));
    }

    [Theory]
    [InlineData("PollInterval", "00:00:00")]
    [InlineData("PollInterval", "01:00:01")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "101")]
    [InlineData("MaxAttempts", "0")]
    [InlineData("MaxAttempts", "11")]
    [InlineData("StaleAfterDays", "0")]
    [InlineData("StaleAfterDays", "3651")]
    [InlineData("IdleRelease", "-00:00:01")]
    [InlineData("IdleRelease", "1.00:00:01")]
    [InlineData("DefaultSources:Airport", "")]
    [InlineData("DefaultSources:Airline", "aero api")]
    [InlineData("DefaultSources:Country", "mledoze,")]
    [InlineData("DefaultSources:Airport", "ourairports+aeroapi")]
    public void AnOutOfRangeValue_FailsValidationOnStart(string key, string value)
    {
        using var provider = Build(new Dictionary<string, string?> { [$"Verification:{key}"] = value });

        var ex = Assert.Throws<OptionsValidationException>(() => ValidateOnStart(provider));

        Assert.Contains(key.Split(':')[^1], ex.Message);
    }

    [Theory]
    [InlineData("PollInterval", "00:00:01")]
    [InlineData("PollInterval", "01:00:00")]
    [InlineData("BatchSize", "1")]
    [InlineData("BatchSize", "100")]
    [InlineData("MaxAttempts", "1")]
    [InlineData("MaxAttempts", "10")]
    [InlineData("StaleAfterDays", "1")]
    [InlineData("StaleAfterDays", "3650")]
    [InlineData("IdleRelease", "00:00:00")]
    [InlineData("IdleRelease", "1.00:00:00")]
    public void TheBoundaryValues_PassValidationOnStart(string key, string value)
    {
        using var provider = Build(new Dictionary<string, string?> { [$"Verification:{key}"] = value });

        ValidateOnStart(provider);
    }

    /// <summary>Default sources are checked by <c>VerificationRequest.TryJoinSources</c>, the rule a
    /// request's own sources follow, so a name a request accepts is accepted here too
    /// (<c>VerificationRequestTests</c> creates a request with the same name).</summary>
    [Fact]
    public void DefaultSources_AcceptTheSameNamesARequestAccepts()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Verification:DefaultSources:Airline"] = "aero-api_2,ourairports"
        });

        ValidateOnStart(provider);

        Assert.Equal(
            new[] { "aero-api_2", "ourairports" },
            provider.GetRequiredService<IOptions<VerificationOptions>>().Value.DefaultSources.For(VerificationEntityType.Airline));
    }

    [Fact]
    public void AddVerificationJob_RegistersTheBackgroundService_AndOneHeartbeatForTheProcess()
    {
        var services = new ServiceCollection();
        services.AddVerificationJob();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ReferenceVerificationBackgroundService));
        Assert.Equal(ServiceLifetime.Singleton,
            Assert.Single(services, d => d.ServiceType == typeof(VerificationJobHeartbeat)).Lifetime);
    }
}
