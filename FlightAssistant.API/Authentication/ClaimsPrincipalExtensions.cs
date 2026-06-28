using System.Security.Claims;
using FlightAssistant.Domain.Common.Exceptions;

namespace FlightAssistant.API.Authentication
{
    public static class ClaimsPrincipalExtensions
    {
        public static Guid GetTravelerId(this ClaimsPrincipal user)
        {
            var id = user.FindFirstValue("oid") ?? user.FindFirstValue("sub");
            if (string.IsNullOrEmpty(id))
                throw new DomainException("No traveler identity claim found.", 401);
            if (!Guid.TryParse(id, out var guid))
                throw new DomainException("Traveler identity claim is not a valid identifier.", 401);
            return guid;
        }

        // Identity profile fields are read from the verified token — never from client input —
        // so Azure AD B2C stays the single source of truth. Claim names assume MapInboundClaims=false.
        public static string? GetEmail(this ClaimsPrincipal user)
            => FirstNonEmptyClaim(user, "email", "emails", "preferred_username");

        public static string? GetDisplayName(this ClaimsPrincipal user)
            => FirstNonEmptyClaim(user, "name");

        public static string? GetCountryCode(this ClaimsPrincipal user)
            => FirstNonEmptyClaim(user, "country", "ctry");

        private static string? FirstNonEmptyClaim(ClaimsPrincipal user, params string[] claimTypes)
        {
            foreach (var claimType in claimTypes)
            {
                var value = user.FindFirstValue(claimType);
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return null;
        }
    }
}
