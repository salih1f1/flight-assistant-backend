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

    [Theory]
    [InlineData("00:00:00")]   // zero — silently disables the only process-wide spend brake
    [InlineData("-00:00:05")]  // negative — same, and nonsensical as an interval
    [InlineData("00:10:00")]   // absurdly long: would drag every derived timeout with it
    public void MinRequestIntervalOutsideRange_FailsValidation(string minRequestInterval)
    {
        // MaxPages, MaxFutureMonths and MaxPastMonths all had a [Range]; this one did not, even
        // though it is the single brake on a metered, per-call billed API — and it is now the value
        // every resilience timeout is derived from, so it is the value most likely to be tuned.
        var options = BuildOptions(new Dictionary<string, string?>
        {
            ["AeroApi:ApiKey"] = "dummy",
            ["AeroApi:MinRequestInterval"] = minRequestInterval
        });

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Theory]
    [InlineData("00:00:12")]
    [InlineData("00:00:45")]
    [InlineData("00:01:30")]
    public void AttemptTimeout_AlwaysExceedsMinRequestInterval_AtAnyConfiguredInterval(string minRequestInterval)
    {
        // The invariant behind the critical defect: AeroApiThrottlingHandler is registered inside
        // the resilience handler, so a throttled wait of up to a whole MinRequestInterval elapses
        // within AttemptTimeout. If the timeout is ever the smaller of the two, every throttled call
        // is killed before it reaches the transport. Deriving it is what makes tuning the interval
        // safe — this asserts the derivation actually holds rather than merely happening to at 12s.
        var options = BuildOptions(new Dictionary<string, string?>
        {
            ["AeroApi:ApiKey"] = "dummy",
            ["AeroApi:MinRequestInterval"] = minRequestInterval
        }).Value;

        Assert.True(options.AttemptTimeout > options.MinRequestInterval);
        Assert.True(options.TotalRequestTimeout > options.AttemptTimeout);
        Assert.True(options.CircuitBreakerSamplingDuration >= options.AttemptTimeout * 2);
        Assert.True(options.HttpClientTimeout > options.TotalRequestTimeout);
    }

    [Theory]
    [InlineData("0")]      // a zero budget makes the panel's gauge meaningless (divide by zero)
    [InlineData("-1")]     // negative — nonsensical as a spend ceiling
    [InlineData("20000")]  // far past any plausible tier; almost certainly a typo'd cents value
    public void MonthlyBudgetUsdOutsideRange_FailsValidation(string budget)
    {
        // Decimal properties take the double-based [Range] overload; this proves it actually binds
        // and validates rather than silently accepting anything.
        var options = BuildOptions(new Dictionary<string, string?>
        {
            ["AeroApi:ApiKey"] = "dummy",
            ["AeroApi:MonthlyBudgetUsd"] = budget
        });

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Fact]
    public void MonthlyBudgetUsd_BindsAConfiguredValue()
    {
        var options = BuildOptions(new Dictionary<string, string?>
        {
            ["AeroApi:ApiKey"] = "dummy",
            ["AeroApi:MonthlyBudgetUsd"] = "12.50"
        });

        Assert.Equal(12.50m, options.Value.MonthlyBudgetUsd);
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
        Assert.Equal(2, value.MaxRetryAttempts);
        Assert.Equal(5m, value.MonthlyBudgetUsd);
        Assert.Equal(TimeSpan.FromMinutes(5), value.SearchCacheTtlPastOrToday);
        Assert.Equal(TimeSpan.FromHours(12), value.SearchCacheTtlFuture);

        // Derived, not configured: a 12s throttle wait plus a 30s network budget per attempt.
        Assert.Equal(TimeSpan.FromSeconds(42), value.AttemptTimeout);
    }

    [Theory]
    [InlineData("00:00:00")]  // zero — would disable the search cache TTL entirely
    [InlineData("-00:00:05")] // negative — nonsensical as a TTL
    [InlineData("02:00:00")]  // past the 1-hour ceiling for "live-ish" past/today data
    public void SearchCacheTtlPastOrTodayOutsideRange_FailsValidation(string ttl)
    {
        var options = BuildOptions(new Dictionary<string, string?>
        {
            ["AeroApi:ApiKey"] = "dummy",
            ["AeroApi:SearchCacheTtlPastOrToday"] = ttl
        });

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Theory]
    [InlineData("00:00:00")]  // zero — would disable the search cache TTL entirely
    [InlineData("00:00:30")]  // below the 5-minute floor
    [InlineData("8.00:00:00")] // past the 7-day ceiling — stale enough to risk a real schedule change
    public void SearchCacheTtlFutureOutsideRange_FailsValidation(string ttl)
    {
        var options = BuildOptions(new Dictionary<string, string?>
        {
            ["AeroApi:ApiKey"] = "dummy",
            ["AeroApi:SearchCacheTtlFuture"] = ttl
        });

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }
}
