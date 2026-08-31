using System.Reflection;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;
using Microsoft.Extensions.Configuration;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// GetAeroApiUsageQueryHandler (Application) prices whatever template strings the repository
/// returns by dictionary lookup, defaulting an unrecognised key to $0 — it deliberately never sees
/// the canonical template list, so a typo'd or forgotten Pricing key never fails the rollup, it just
/// shows a silent $0 line. This test is the guard rail for that: it lives here, not in Application,
/// because both <see cref="AeroApiEndpointTemplate"/> (the canonical list) and
/// <see cref="AeroApiOptions"/> (the Pricing dictionary's shape) are Infrastructure-only types.
///
/// <para>
/// Two guards, chained. The reflection guard proves the canonical template list is a subset of
/// <see cref="ExpectedPricing"/>; the binding guards prove <see cref="ExpectedPricing"/> matches
/// the <c>AeroApi:Pricing</c> section of the shipped <c>appsettings.json</c>, bound the same way
/// the host binds it. Neither half is useful alone — a fixture that only agrees with itself cannot
/// fail, which is precisely what the earlier version of this file did.
/// </para>
/// </summary>
public class AeroApiPricingCoverageTests
{
    // The set of Pricing keys every environment is expected to configure via AeroApi:Pricing.
    // Hardcoded independently of AeroApiEndpointTemplate's own fields (reflected below) — the whole
    // point is that adding a new template constant without adding a price for it here breaks this
    // test, forcing whoever adds the endpoint to make a deliberate pricing decision (even if that
    // decision is "$0") instead of it silently falling through the handler's own zero-default.
    private static readonly IReadOnlyDictionary<string, decimal> ExpectedPricing = new Dictionary<string, decimal>
    {
        [AeroApiEndpointTemplate.Schedules] = 0.005m,
        [AeroApiEndpointTemplate.Flights] = 0.005m,
        [AeroApiEndpointTemplate.Airports] = 0.005m,
    };

    private static List<string> CanonicalTemplates()
    {
        // Every public const string on AeroApiEndpointTemplate except Unrecognised, which is the
        // deliberately open-ended "we don't know what this is" bucket and is never priced.
        return typeof(AeroApiEndpointTemplate)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(template => template != AeroApiEndpointTemplate.Unrecognised)
            .ToList();
    }

    [Fact]
    public void CanonicalTemplates_ReflectionFindsTheKnownNamedEndpoints()
    {
        // Guards the guard: if reflection ever stops finding these constants (e.g. they become
        // non-const), the coverage test below would vacuously pass over an empty list.
        var templates = CanonicalTemplates();

        Assert.Contains(AeroApiEndpointTemplate.Schedules, templates);
        Assert.Contains(AeroApiEndpointTemplate.Flights, templates);
        Assert.Contains(AeroApiEndpointTemplate.Airports, templates);
        Assert.DoesNotContain(AeroApiEndpointTemplate.Unrecognised, templates);
    }

    [Fact]
    public void EveryCanonicalEndpointTemplate_HasARecognisedPricingKey()
    {
        var canonicalTemplates = CanonicalTemplates();

        foreach (var template in canonicalTemplates)
        {
            Assert.True(
                ExpectedPricing.ContainsKey(template),
                $"'{template}' is a canonical AeroApiEndpointTemplate but has no entry in the " +
                "expected AeroApi:Pricing configuration. Add a price (or an explicit $0 entry) " +
                "both here and in the real AeroApi:Pricing config, or the usage rollup will silently " +
                "bill it at $0.");
        }
    }

    /// <summary>
    /// Binds the REAL <c>FlightAssistant.API/appsettings.json</c> — copied into this project's
    /// output by the csproj, not project-referenced — exactly the way
    /// <c>services.AddOptions&lt;AeroApiOptions&gt;().BindConfiguration("AeroApi")</c> does at
    /// startup. Anything asserted against this is asserted against what an operator actually gets
    /// on deploy.
    /// </summary>
    private static AeroApiOptions BindShippedAeroApiOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var options = new AeroApiOptions();
        configuration.GetSection("AeroApi").Bind(options);
        return options;
    }

    [Fact]
    public void ShippedAppSettings_PricesEveryExpectedTemplate_ByExactStringEquality()
    {
        // The predecessor of this test built its own dictionary from ExpectedPricing and then
        // looked ExpectedPricing's keys up in it — it exercised Dictionary's copy constructor and
        // could not fail. It now binds the shipped configuration instead, so a template added
        // without a price, a key typo'd in appsettings.json (different casing, stray whitespace,
        // a missing brace) or the whole section going missing all trip here. None of those throw
        // in production: an unmatched key just prices at $0, which is the silent failure this
        // guards.
        var options = BindShippedAeroApiOptions();

        Assert.NotEmpty(options.Pricing);

        foreach (var (template, price) in ExpectedPricing)
        {
            Assert.True(
                options.Pricing.TryGetValue(template, out var actual),
                $"'{template}' has no AeroApi:Pricing entry in the shipped appsettings.json, so the " +
                "usage rollup would silently bill it at $0.");
            Assert.Equal(price, actual);
        }
    }

    [Fact]
    public void ShippedAppSettings_PricesNothingThatIsNotACanonicalTemplate()
    {
        // The other direction: a stale key left behind after a template was renamed prices nothing
        // and would sit there looking configured. Bound config, again, not a fixture.
        var options = BindShippedAeroApiOptions();
        var canonicalTemplates = CanonicalTemplates();

        foreach (var configuredTemplate in options.Pricing.Keys)
        {
            Assert.Contains(configuredTemplate, canonicalTemplates);
        }
    }
}
