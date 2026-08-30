using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// Exercises the exact registration pattern used in <c>DependencyInjection.AddInfrastructure</c> —
/// <c>AddOptions&lt;AeroApiOptions&gt;().BindConfiguration("AeroApi").ValidateDataAnnotations().ValidateOnStart()</c>
/// — in isolation, so a missing/invalid <c>AeroApi:ApiKey</c> is proven to fail fast rather than
/// surfacing as a confusing null-reference deep inside a provider on first use.
/// </summary>
public class AeroApiOptionsTests
{
    private static IOptions<AeroApiOptions> BuildOptions(Dictionary<string, string?> configValues)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<AeroApiOptions>()
            .BindConfiguration("AeroApi")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services.BuildServiceProvider().GetRequiredService<IOptions<AeroApiOptions>>();
    }

    [Fact]
    public void MissingApiKey_FailsValidation()
    {
        var options = BuildOptions(new Dictionary<string, string?>());

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Fact]
    public void EmptyApiKey_FailsValidation()
    {
        var options = BuildOptions(new Dictionary<string, string?> { ["AeroApi:ApiKey"] = "" });

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void MaxPagesOutsideRange_FailsValidation(int maxPages)
    {
        var options = BuildOptions(new Dictionary<string, string?>
        {
            ["AeroApi:ApiKey"] = "dummy",
            ["AeroApi:MaxPages"] = maxPages.ToString()
        });

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Fact]
    public void ValidApiKey_BindsWithDocumentedDefaults()
    {
        var options = BuildOptions(new Dictionary<string, string?> { ["AeroApi:ApiKey"] = "dummy" });

        var value = options.Value;

        Assert.Equal("dummy", value.ApiKey);
        Assert.Equal("https://aeroapi.flightaware.com/aeroapi/", value.BaseUrl);
        Assert.Equal(4, value.MaxPages);
        Assert.Equal(TimeSpan.FromSeconds(12), value.MinRequestInterval);
        Assert.Equal(12, value.MaxFutureMonths);
        Assert.Equal(3, value.MaxPastMonths);
        Assert.Equal(30, value.TimeoutSeconds);
    }
}
