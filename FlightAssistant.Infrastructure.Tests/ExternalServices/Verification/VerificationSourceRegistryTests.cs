using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices.Verification;

/// <summary>
/// <see cref="VerificationSourceRegistry"/> against fake <see cref="IVerificationSource"/>s --
/// pure name resolution and selective release, no HTTP involved (that is each source's own
/// responsibility, covered in its own test file).
/// </summary>
public class VerificationSourceRegistryTests
{
    private static IVerificationSource FakeSource(string name)
    {
        var source = Substitute.For<IVerificationSource>();
        source.Name.Returns(name);
        source.CostPerRowUsd.Returns(0m);
        source.SupportedEntityTypes.Returns(new[] { VerificationEntityType.Airport });
        return source;
    }

    [Fact]
    public void TryGet_ResolvesARegisteredSourceByName()
    {
        var a = FakeSource("a");
        var registry = new VerificationSourceRegistry(new[] { a, FakeSource("b") });

        Assert.Same(a, registry.TryGet("a"));
    }

    [Fact]
    public void TryGet_ReturnsNull_ForAnUnregisteredName()
    {
        var registry = new VerificationSourceRegistry(new[] { FakeSource("a") });

        Assert.Null(registry.TryGet("aeroapi"));
    }

    [Fact]
    public void TryGet_ReturnsNull_ForABlankName()
    {
        var registry = new VerificationSourceRegistry(new[] { FakeSource("a") });

        Assert.Null(registry.TryGet(""));
    }

    [Fact]
    public void Sources_EnumeratesEveryRegisteredSource()
    {
        var a = FakeSource("a");
        var b = FakeSource("b");
        var registry = new VerificationSourceRegistry(new[] { a, b });

        Assert.Equal(new[] { a, b }, registry.Sources);
    }

    [Fact]
    public void ReleaseUnused_ReleasesOnlySourcesNotInTheGivenSet()
    {
        var inUse = FakeSource("in-use");
        var unused = FakeSource("unused");
        var registry = new VerificationSourceRegistry(new[] { inUse, unused });

        registry.ReleaseUnused(new[] { "in-use" });

        inUse.DidNotReceive().ReleaseCache();
        unused.Received(1).ReleaseCache();
    }

    [Fact]
    public void ReleaseUnused_WithAnEmptySet_ReleasesEverySource()
    {
        var a = FakeSource("a");
        var b = FakeSource("b");
        var registry = new VerificationSourceRegistry(new[] { a, b });

        registry.ReleaseUnused(Array.Empty<string>());

        a.Received(1).ReleaseCache();
        b.Received(1).ReleaseCache();
    }
}
