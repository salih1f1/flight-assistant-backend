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

    [Fact]
    public void GetAdminActorId_OidClaim_ReturnsItUnparsed()
    {
        Assert.Equal("admin-oid-1", UserWith("oid", "admin-oid-1").GetAdminActorId());
    }

    [Fact]
    public void GetAdminActorId_BothClaims_PrefersOid()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "the-sub"), new Claim("oid", "the-oid")], "test"));

        Assert.Equal("the-oid", user.GetAdminActorId());
    }

    [Fact]
    public void GetAdminActorId_NoOid_FallsBackToSub()
    {
        Assert.Equal("the-sub", UserWith("sub", "the-sub").GetAdminActorId());
    }

    [Fact]
    public void GetAdminActorId_NoOidOrSub_IsAdmin()
    {
        Assert.Equal("admin", UserWithNoClaims().GetAdminActorId());
    }
}
