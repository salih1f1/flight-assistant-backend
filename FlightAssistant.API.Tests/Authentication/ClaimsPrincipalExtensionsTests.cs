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

    [Fact]
    public void GetEmail_EmailClaim_ReturnsIt()
    {
        Assert.Equal("alice@example.com", UserWith("email", "alice@example.com").GetEmail());
    }

    [Fact]
    public void GetEmail_NoEmail_FallsBackToPreferredUsername()
    {
        Assert.Equal("alice@example.com", UserWith("preferred_username", "alice@example.com").GetEmail());
    }

    [Fact]
    public void GetEmail_NoEmailOrPreferredUsername_FallsBackToEmails()
    {
        Assert.Equal("alice@example.com", UserWith("emails", "alice@example.com").GetEmail());
    }

    [Fact]
    public void GetEmail_EveryClaimPresent_PrefersEmail()
    {
        var user = UserWithClaims(
            ("emails", "third@example.com"),
            ("preferred_username", "second@example.com"),
            ("email", "first@example.com"));

        Assert.Equal("first@example.com", user.GetEmail());
    }

    [Fact]
    public void GetEmail_BlankEmailClaim_FallsThroughToTheNextClaim()
    {
        var user = UserWithClaims(("email", "   "), ("preferred_username", "alice@example.com"));

        Assert.Equal("alice@example.com", user.GetEmail());
    }

    [Fact]
    public void GetEmail_NoEmailClaimOfAnyKind_ThrowsDomainException401()
    {
        var ex = Assert.Throws<DomainException>(() => UserWithNoClaims().GetEmail());

        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public void GetEmail_EveryEmailClaimBlank_ThrowsDomainException401()
    {
        var user = UserWithClaims(("email", " "), ("preferred_username", ""), ("emails", "\t"));

        var ex = Assert.Throws<DomainException>(() => user.GetEmail());

        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public void GetDisplayName_NameClaim_ReturnsIt()
    {
        Assert.Equal("Salih", UserWith("name", "Salih").GetDisplayName());
    }

    [Fact]
    public void GetDisplayName_NoNameClaim_IsNull()
    {
        Assert.Null(UserWithNoClaims().GetDisplayName());
    }

    [Fact]
    public void GetDisplayName_BlankNameClaim_IsNull()
    {
        Assert.Null(UserWith("name", "   ").GetDisplayName());
    }
}
