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

        /// <summary>
        /// The admin actor an admin endpoint records as <c>VerifiedBy</c> or <c>requested_by</c>: the
        /// <c>oid</c> claim, then <c>sub</c>, then <c>"admin"</c>. The one copy of that rule for endpoints;
        /// the <c>/admin</c> group filter reads the same two claims for <c>IAdminContext.ActorId</c> but
        /// keeps it null, because no actor there means no audit row.
        /// </summary>
        public static string GetAdminActorId(this ClaimsPrincipal user) =>
            user.FindFirstValue("oid") ?? user.FindFirstValue("sub") ?? "admin";
    }
}
