using System.ComponentModel.DataAnnotations;
using FlightAssistant.API.Infrastructure;

namespace FlightAssistant.API.Tests.Configuration;

public class ReferenceProvisioningOptionsTests
{
    private static IReadOnlyList<ValidationResult> Validate(ReferenceProvisioningOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(Validate(new ReferenceProvisioningOptions()));
    }

    [Fact]
    public void Defaults_AreEnabledThirtySecondsAndADay()
    {
        var options = new ReferenceProvisioningOptions();

        Assert.True(options.Enabled);
        // 30 s, not 10: the airline lookup waits on the shared AeroApiThrottleGate for up to a
        // whole AeroApi:MinRequestInterval (12 s by default) before its request is even sent, and a
        // cold usage-report read for the budget guard can add a second such wait. A 10 s budget was
        // systematically shorter than the waiting it had to sit through.
        Assert.Equal(TimeSpan.FromSeconds(30), options.Budget);
        Assert.Equal(TimeSpan.FromHours(24), options.NegativeCacheTtl);
    }

    [Fact]
    public void Budget_AtTheTwoMinuteCeiling_IsValid()
    {
        var options = new ReferenceProvisioningOptions { Budget = TimeSpan.FromMinutes(2) };

        Assert.Empty(Validate(options));
    }

    [Theory]
    [InlineData("00:00:00")]   // below the 1 s floor — a zero budget would fail every provision
    [InlineData("00:02:01")]   // above the 120 s ceiling — a traveler request must not hang this long
    public void Budget_OutsideRange_FailsValidation(string budget)
    {
        var options = new ReferenceProvisioningOptions { Budget = TimeSpan.Parse(budget) };

        Assert.NotEmpty(Validate(options));
    }

    [Fact]
    public void NegativeCacheTtl_MayBeZero_WhichDisablesTheCache()
    {
        var options = new ReferenceProvisioningOptions { NegativeCacheTtl = TimeSpan.Zero };

        Assert.Empty(Validate(options));
    }

    [Fact]
    public void NegativeCacheTtl_BeyondSevenDays_FailsValidation()
    {
        var options = new ReferenceProvisioningOptions { NegativeCacheTtl = TimeSpan.FromDays(8) };

        Assert.NotEmpty(Validate(options));
    }
}
