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

    private static ClaimsPrincipal UserWithClaims(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test");
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
    public void GetEmail_EmailClaim_ReturnsValue()
    {
        var user = UserWith("email", "alice@example.com");
        Assert.Equal("alice@example.com", user.GetEmail());
    }

    [Fact]
    public void GetEmail_NoEmailClaim_FallsBackToPreferredUsername()
    {
        var user = UserWith("preferred_username", "bob@example.com");
        Assert.Equal("bob@example.com", user.GetEmail());
    }

    [Fact]
    public void GetEmail_NoClaims_ReturnsNull()
    {
        Assert.Null(UserWithNoClaims().GetEmail());
    }

    [Fact]
    public void GetDisplayName_RealNameClaim_ReturnsName()
    {
        var user = UserWithClaims(("name", "Alice Smith"), ("preferred_username", "alice@example.com"));
        Assert.Equal("Alice Smith", user.GetDisplayName());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Unknown")]
    [InlineData("  ")]
    public void GetDisplayName_NamePlaceholderOrEmpty_FallsBackToPreferredUsername(string name)
    {
        var user = UserWithClaims(("name", name), ("preferred_username", "bob@example.com"));
        Assert.Equal("bob@example.com", user.GetDisplayName());
    }

    [Fact]
    public void GetDisplayName_NoNameClaim_FallsBackToPreferredUsername()
    {
        var user = UserWith("preferred_username", "carol@example.com");
        Assert.Equal("carol@example.com", user.GetDisplayName());
    }

    [Fact]
    public void GetDisplayName_NoNameOrUsername_ReturnsNull()
    {
        Assert.Null(UserWithNoClaims().GetDisplayName());
    }
}
