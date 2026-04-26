using System.Security.Claims;

namespace FlightAssistant.API.Authentication
{
    public static class ClaimsPrincipalExtensions
    {
        public static Guid GetTravelerId(this ClaimsPrincipal user)
        {
            var id = user.FindFirstValue("oid") ?? user.FindFirstValue("sub");
            if (string.IsNullOrEmpty(id))
                throw new InvalidOperationException("No traveler identity claim found.");
            return Guid.Parse(id);
        }
    }
}
