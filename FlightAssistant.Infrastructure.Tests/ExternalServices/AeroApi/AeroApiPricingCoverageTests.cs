using System.Reflection;
using FlightAssistant.Infrastructure.ExternalServices.AeroApi;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.AeroApi;

/// <summary>
/// GetAeroApiUsageQueryHandler (Application) prices whatever template strings the repository
/// returns by dictionary lookup, defaulting an unrecognised key to $0 — it deliberately never sees
/// the canonical template list, so a typo'd or forgotten Pricing key never fails the rollup, it just
/// shows a silent $0 line. This test is the guard rail for that: it lives here, not in Application,
/// because both <see cref="AeroApiEndpointTemplate"/> (the canonical list) and
/// <see cref="AeroApiOptions"/> (the Pricing dictionary's shape) are Infrastructure-only types.
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

    [Fact]
    public void AeroApiOptions_Pricing_LooksUpEveryExpectedKeyByExactStringEquality()
    {
        // A typo'd key in appsettings/Key Vault (different casing, stray whitespace) would bind
        // successfully and then silently miss on every lookup — AeroApiOptions.Pricing costs an
        // unmatched key at $0 rather than throwing. This proves the literal strings operators are
        // expected to configure resolve through the same dictionary type the options class exposes.
        var options = new AeroApiOptions
        {
            Pricing = new Dictionary<string, decimal>(ExpectedPricing),
        };

        foreach (var (template, price) in ExpectedPricing)
        {
            Assert.True(options.Pricing.TryGetValue(template, out var actual));
            Assert.Equal(price, actual);
        }
    }
}
