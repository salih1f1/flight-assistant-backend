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
    }
}
