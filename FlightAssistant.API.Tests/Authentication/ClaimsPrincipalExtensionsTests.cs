using System.Security.Claims;
using FlightAssistant.API.Authentication;
using FlightAssistant.Domain.Common.Exceptions;

namespace FlightAssistant.API.Tests.Authentication;

public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal UserWith(string claimType, string value)
    {
        var identity = new ClaimsIdentity([new Claim(claimType, value)], "test");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal UserWithNoClaims()
    {
        var identity = new ClaimsIdentity([], "test");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void GetTravelerId_ValidOidClaim_ReturnsGuid()
    {
        var expected = Guid.NewGuid();
        var user = UserWith("oid", expected.ToString());

        var result = user.GetTravelerId();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetTravelerId_NoOid_FallsBackToSub()
    {
        var expected = Guid.NewGuid();
        var user = UserWith("sub", expected.ToString());

        var result = user.GetTravelerId();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetTravelerId_NoOidOrSub_ThrowsDomainException401()
    {
        var user = UserWithNoClaims();

        var ex = Assert.Throws<DomainException>(() => user.GetTravelerId());

        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public void GetTravelerId_MalformedOidGuid_ThrowsDomainException401()
    {
        var user = UserWith("oid", "not-a-guid");

        var ex = Assert.Throws<DomainException>(() => user.GetTravelerId());

        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public void GetTravelerId_MalformedSubGuid_ThrowsDomainException401()
    {
        var user = UserWith("sub", "not-a-guid");

        var ex = Assert.Throws<DomainException>(() => user.GetTravelerId());

        Assert.Equal(401, ex.StatusCode);
    }
}
