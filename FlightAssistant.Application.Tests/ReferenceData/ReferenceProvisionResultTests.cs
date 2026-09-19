using System.Reflection;
using FlightAssistant.Application.ReferenceData;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Tests.ReferenceData;

/// <summary>
/// #510: the factories are the only way to build a <see cref="ReferenceProvisionResult"/>, so
/// <c>TrackFlightCommandHandler.ToDomainException</c>'s <c>FailedType!.Value</c> and
/// <c>FailedCode!</c> cannot be handed a <c>NotFound</c> that names no entity — which would be a
/// <see cref="NullReferenceException"/> inside <c>POST /flights</c>.
///
/// <para>
/// The invariant is enforced by the compiler, so the tests that matter here are reflective: the
/// thing worth catching is a future change that reopens construction, not a value the factories
/// produce. Both checks were mutation-tested against a public constructor and an <c>init</c>
/// setter, and each fails on the change it names.
/// </para>
/// </summary>
public class ReferenceProvisionResultTests
{
    [Fact]
    public void TheTypeExposesNoPublicConstructor()
    {
        var publicConstructors = typeof(ReferenceProvisionResult)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(publicConstructors);
    }

    /// <summary>
    /// The other half: an <c>init</c> or <c>set</c> accessor would let a <c>with</c> expression
    /// rebuild the record into a state the factories refuse, reopening the hole from a direction a
    /// constructor check cannot see.
    /// </summary>
    [Fact]
    public void NoPropertyIsSettable_SoNoWithExpressionCanRebuildIt()
    {
        var settable = typeof(ReferenceProvisionResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is not null)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(settable);
    }

    /// <summary>
    /// What the guarantee is actually for: on both failure statuses the two fields
    /// <c>ToDomainException</c> dereferences are populated, and on the success status they are not.
    /// </summary>
    [Theory]
    [InlineData(VerificationEntityType.Airline, "BA")]
    [InlineData(VerificationEntityType.Airport, "LHR")]
    [InlineData(VerificationEntityType.Country, "GB")]
    public void NotFound_NamesTheEntity(VerificationEntityType type, string code)
    {
        var result = ReferenceProvisionResult.NotFound(type, code);

        Assert.Equal(ReferenceProvisionStatus.NotFound, result.Status);
        Assert.Equal(type, result.FailedType);
        Assert.Equal(code, result.FailedCode);
        Assert.Null(result.DiagnosticDetail);
    }

    [Fact]
    public void Unavailable_NamesTheEntityAndCarriesTheCause()
    {
        var result = ReferenceProvisionResult.Unavailable(
            VerificationEntityType.Airline, "BA", "ourairports download failed: HTTP 503");

        Assert.Equal(ReferenceProvisionStatus.Unavailable, result.Status);
        Assert.Equal(VerificationEntityType.Airline, result.FailedType);
        Assert.Equal("BA", result.FailedCode);
        Assert.Equal("ourairports download failed: HTTP 503", result.DiagnosticDetail);
    }

    [Fact]
    public void Provisioned_NamesNoEntity()
    {
        var result = ReferenceProvisionResult.Provisioned;

        Assert.Equal(ReferenceProvisionStatus.Provisioned, result.Status);
        Assert.Null(result.FailedType);
        Assert.Null(result.FailedCode);
        Assert.Null(result.DiagnosticDetail);
    }

    /// <summary>Value equality still works — it comes from being a record, not from the
    /// constructor's accessibility, and the provisioner's own tests compare results.</summary>
    [Fact]
    public void ValueEqualitySurvivesThePrivateConstructor()
    {
        Assert.Equal(
            ReferenceProvisionResult.NotFound(VerificationEntityType.Airport, "LHR"),
            ReferenceProvisionResult.NotFound(VerificationEntityType.Airport, "LHR"));

        Assert.NotEqual(
            ReferenceProvisionResult.NotFound(VerificationEntityType.Airport, "LHR"),
            ReferenceProvisionResult.NotFound(VerificationEntityType.Airport, "JFK"));
    }
}
